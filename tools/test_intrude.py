"""Joining a match that is already running ("intrusion").

A host starts a survival match alone. A second player then joins the room from the lobby,
asks to enter the running match (cRoom 14), loads, and is admitted:

    joiner                         server                          host
    cLobby 01 join        ->       sRoom 05 (state 2 = playing)
    cRoom 14 IntrusionGame ->      sGame 00 + sMode 0F
    cGame 09 loaded       ->                                 ->    sHost 01 (player added)
                                   sMode 10 (after the host)
                                                              <-   cHost 0C event 0 + player id
                                   sMode 11

    python tools/test_intrude.py
"""
import socket
import struct
import sys
import time

from test_client import first_channel, Client, check, ws


def drain(client, wait=0.5):
    client.sock.settimeout(wait)
    replies = []
    try:
        while True:
            replies.append(client.recv())
    except (socket.timeout, OSError):
        pass
    return [reply for reply in replies if reply[:1] != b"\x00"]


def player(tag):
    stamp = format(int(time.time() * 10) % 0xFFFFFF, "x")
    c = Client()
    c.finish_handshake(c.login(tag + stamp, "intrude12"))
    drain(c)
    c.send(bytes.fromhex("2d03") + ws(tag.upper() + stamp))
    uid = next(reply[1:5] for reply in drain(c) if reply[:1] == b"\x06")
    c.send(bytes.fromhex("2d0401f6010400160000000000"))
    drain(c)
    c.send(bytes.fromhex("2e00"))
    drain(c)
    c.send(bytes.fromhex("3118"))
    drain(c)
    c.send(bytes.fromhex("3203") + struct.pack("<H", first_channel()))
    drain(c)
    return c, uid


def has(replies, prefix):
    return any(reply[:len(prefix)] == prefix for reply in replies)


def main():
    ok = True
    host, host_id = player("ih")
    host.send(bytes.fromhex("3200") + ws("intrude test") + bytes([0, 0, 2, 6, 0, 0, 1, 1]))
    entered = next(reply for reply in drain(host) if reply[:2] == b"\x10\x05")
    room = entered[3:5]
    host.send(bytes.fromhex("330c")); drain(host)
    host.send(bytes.fromhex("330e"))
    game = next(reply for reply in drain(host) if reply[:2] == b"\x15\x00")
    room4 = game[2:6]

    late, late_id = player("il")
    late.send(bytes.fromhex("3201") + room + bytes(3))
    refused = drain(late)
    ok &= check("join while the match is still loading: refused (sLobby 02 + 73)", has(refused, b"\x0f\x02\x49"), str([r.hex()[:8] for r in refused]))

    host.send(bytes.fromhex("3709")); drain(host)
    host.send(b"\x36\x0c" + room4 + b"\x00" + host_id)
    ok &= check("host is in the match (sMode 11)", has(drain(host), b"\x0e\x11"))

    late.send(bytes.fromhex("3201") + room + bytes(3))
    joined = drain(late)
    info = next((reply for reply in joined if reply[:2] == b"\x10\x05"), b"")
    title_end = 5 + 2 + 2 * struct.unpack_from("<H", info, 5)[0] if info else 0
    ok &= check("join a running room: sRoom 05 + sMode 0C", bool(info) and has(joined, b"\x0e\x0c"), str([r.hex()[:8] for r in joined]))
    ok &= check("room info says state 2 (playing)", bool(info) and info[title_end + 3] == 2, info[title_end:title_end + 4].hex())
    update = next((reply for reply in joined if reply[:2] == b"\x10\x08"), b"")
    state_at = 3 + 2 + 2 * struct.unpack_from("<H", update, 3)[0] + 3 if update else 0
    ok &= check("room update keeps state 2", bool(update) and update[state_at] == 2, update[:state_at + 1].hex())
    host_saw = drain(host)
    ok &= check("host hears the new room member (sRoom 06)", has(host_saw, b"\x10\x06" + late_id))

    late.send(bytes.fromhex("3314"))
    start = drain(late)
    game2 = next((reply for reply in start if reply[:2] == b"\x15\x00"), b"")
    ok &= check("cRoom 14 -> sGame 00 with the map being played", game2[2:] != b"" and game2[2:24] == game[2:24] and game2[24:28] == game[24:28], game2.hex())
    ok &= check("cRoom 14 -> sMode 0F (loading screen)", has(start, b"\x0e\x0f"))
    ok &= check("host is not told before the joiner has loaded", not has(drain(host, 0.3), b"\x14\x01"))

    late.send(bytes.fromhex("3709"))
    time.sleep(0.3)
    told = drain(host)
    added = next((reply for reply in told if reply[:2] == b"\x14\x01"), b"")
    expected = b"\x14\x01" + room4 + b"\x02\x01" + struct.pack("<H", 2) + b"\x02" + late_id
    ok &= check("host gets sHost 01: room, 02 (player table), 01 (add), bitset slot 1, record", added[:len(expected)] == expected, added[:20].hex())
    ok &= check("record ends with 'is a player' = 1", added[-1:] == b"\x01" and len(added) > 330, str(len(added)))
    ok &= check("joiner gets sMode 10 after the host was told", has(drain(late, 1.2), b"\x0e\x10"))

    host.send(b"\x36\x0c" + room4 + b"\x00" + late_id)
    ok &= check("host admits the joiner -> joiner gets sMode 11", has(drain(late), b"\x0e\x11"))

    late.send(bytes.fromhex("3314"))
    ok &= check("asking again while in the match: refused (sRoom 09)", has(drain(late), b"\x10\x09"))

    late.send(bytes.fromhex("3706"))
    drain(late)
    removed = next((reply for reply in drain(host) if reply[:2] == b"\x14\x01"), b"")
    ok &= check("joiner leaves -> host gets sHost 01 remove (02 02, bitset slot 1)",
                removed == b"\x14\x01" + room4 + b"\x02\x02" + struct.pack("<H", 2) + b"\x02", removed.hex())

    host.send(b"\x36\x13" + room4); drain(host)
    host.send(b"\x36\x0a" + room4); drain(host)
    host.send(bytes.fromhex("3306")); drain(host)
    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
