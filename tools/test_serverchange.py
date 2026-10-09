"""Server change and the GM observer screen. Needs a server whose config has

    "Staff": {"gmobs": "GameMaster"}  and one channel bound to the second lobby port, e.g. {"Id": 7301, "LobbyPort": 26110}

    sTransServer 00   u16 channel, addr server, u32 key      go to that server
    cClientTransferInfo (first packet on the new connection)  u16 version, u16, u32, u32, str login id, wstr nickname, u8, u64 session key
    sUserRestart      (no body)                               the change is complete
    sMode 13 + sObserver 00 (the record of sGame 00)          /gm_observe on a running match

    BB_LOBBY_PORT=26100 BB_SECOND_PORT=26110 BB_BOUND_CHANNEL=7301 python tools/test_serverchange.py
"""
import os
import socket
import struct
import sys
import time

from test_client import Client, check, first_channel, s8, ws
from test_intrude import drain, has

SECOND_PORT = int(os.environ.get("BB_SECOND_PORT", "26110"))
BOUND_CHANNEL = int(os.environ.get("BB_BOUND_CHANNEL", "7301"))


def enter(login_id, nickname):
    """Logs in, makes the character and enters the first channel. Returns (client, user id, session key)."""
    c = Client()
    key = c.finish_handshake(c.login(login_id, "intrude12"))
    drain(c)
    c.send(bytes.fromhex("2d03") + ws(nickname))
    uid = next((reply[1:5] for reply in drain(c) if reply[:1] == b"\x06"), None)
    if uid is None:
        c.send(bytes.fromhex("2e00"))
        uid = next(reply[1:5] for reply in drain(c) if reply[:1] == b"\x06")
    c.send(bytes.fromhex("2d0401f6010400160000000000")); drain(c)
    c.send(bytes.fromhex("2e00")); drain(c)
    c.send(bytes.fromhex("3118")); drain(c)
    c.send(bytes.fromhex("3203") + struct.pack("<H", first_channel())); drain(c)
    return c, uid, key


def server_change(ok):
    stamp = format(int(time.time() * 10) % 0xFFFFF, "x")
    login_id, nickname = "sc" + stamp, "SC" + stamp
    c, uid, session_key = enter(login_id, nickname)

    c.send(bytes.fromhex("3203") + struct.pack("<H", BOUND_CHANNEL))
    replies = drain(c, 1.5)
    move = next((reply for reply in replies if reply[:2] == b"\x18\x00"), b"")
    ok &= check("entering a channel of the other port: sTransServer 00 with the channel",
                len(move) == 14 and struct.unpack_from("<H", move, 2)[0] == BOUND_CHANNEL, str([reply.hex() for reply in replies]))
    if not move:
        return ok
    # an address on the wire is the four bytes reversed and inverted, then the port inverted
    address = socket.inet_ntoa(bytes(b ^ 0xFF for b in move[7:3:-1]))
    port = struct.unpack_from("<H", move, 8)[0] ^ 0xFFFF
    transfer_key = move[10:14]
    ok &= check("it names the second lobby port", (address, port) == ("127.0.0.1", SECOND_PORT), "%s:%d" % (address, port))
    ok &= check("the channel is not entered on the old connection", not has(replies, b"\x05\x02"))

    new = Client(port)
    new.sock.sendall(b"\x27" + struct.pack("<HH", 105, BOUND_CHANNEL) + uid + transfer_key + s8(login_id) + ws(nickname)
                     + b"\x00" + struct.pack("<Q", session_key))
    new.finish_handshake(new.sock.recv(256))
    arrived = drain(new, 1.5)
    ok &= check("the new connection starts with sUserRestart (07, no body)", arrived[:1] == [b"\x07"], str([reply.hex()[:16] for reply in arrived]))
    ok &= check("then the channel (sServer 02), the player count and the room list",
                has(arrived, b"\x05\x02" + struct.pack("<H", BOUND_CHANNEL)) and has(arrived, b"\x0f\x00") and has(arrived, b"\x0d\x00"),
                str([reply.hex()[:16] for reply in arrived]))
    ok &= check("no fresh start (sUserStart / sMode) is sent", not has(arrived, b"\x06") and not has(arrived, b"\x0e"))

    c.sock.settimeout(3)
    try:
        closed, told = False, False
        while True:
            reply = c.recv()
            told |= reply[:2] == b"\x1b\x00"
    except (ConnectionError, socket.timeout, OSError, AssertionError) as error:
        closed = not isinstance(error, socket.timeout)
    ok &= check("the old connection is closed without a 'connected twice' dialog", closed and not told)

    new.send(bytes.fromhex("3203") + struct.pack("<H", first_channel()))
    back = drain(new, 1.5)
    ok &= check("an unbound channel is entered in place, without another change",
                has(back, b"\x05\x02" + struct.pack("<H", first_channel())) and not has(back, b"\x18"), str([reply.hex()[:12] for reply in back]))
    return ok


def gm_observe(ok):
    stamp = format(int(time.time() * 10) % 0xFFFFF, "x")
    host, host_id, _ = enter("oh" + stamp, "OH" + stamp)
    host.send(bytes.fromhex("3200") + ws("watched") + bytes([0, 0, 2, 6, 0, 0, 1, 1]))
    room = next(reply for reply in drain(host) if reply[:2] == b"\x10\x05")[3:5]
    host.send(bytes.fromhex("330c")); drain(host)
    host.send(bytes.fromhex("330e"))
    game = next(reply for reply in drain(host) if reply[:2] == b"\x15\x00")
    host.send(bytes.fromhex("3709")); drain(host)
    host.send(b"\x36\x0c" + game[2:6] + b"\x00" + host_id)
    ok &= check("the host is in the match", has(drain(host), b"\x0e\x11"))

    gm, gm_id, _ = enter("gmobs", "GmObs")
    gm.send(bytes.fromhex("3202") + room)
    replies = drain(gm, 1.5)
    kinds = [reply[:2] for reply in replies]
    ok &= check("/gm_observe on a running match: sMode 13, then sObserver 00",
                b"\x0e\x13" in kinds and b"\x16\x00" in kinds and kinds.index(b"\x0e\x13") < kinds.index(b"\x16\x00"),
                str([reply.hex()[:12] for reply in replies]))
    record = next((reply for reply in replies if reply[:2] == b"\x16\x00"), b"")
    ok &= check("sObserver 00 carries the record of sGame 00 (room, host, addresses, map, rule)", record[2:] and record[2:14] == game[2:14],
                record.hex() + " / " + game.hex())
    ok &= check("no room screen is opened (no sRoom 05, no waiting-room mode)", b"\x10\x05" not in kinds and b"\x0e\x0c" not in kinds)
    ok &= check("the host is told about the observer at once (sHost)", any(reply[:1] == b"\x14" and gm_id in reply for reply in drain(host)))

    gm.send(bytes.fromhex("3708"))
    host.send(b"\x36\x0c" + game[2:6] + b"\x00" + gm_id)
    ok &= check("in the match the game master stays on the observer screen (no sMode 11)", not has(drain(gm, 1.5), b"\x0e\x11"))

    gm.send(bytes.fromhex("3707"))
    ok &= check("leaving (cGame 07) brings the lobby back (sMode 03)", has(drain(gm, 1.5), b"\x0e\x03"))
    return ok


def main():
    ok = server_change(True)
    ok = gm_observe(ok)
    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
