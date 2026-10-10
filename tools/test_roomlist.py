"""The room list of a player waiting in the lobby follows the rooms without a refresh.

    sRoomList 01  u16 id, wstr title, u8 kind, u8 players, u8 max, u8 state, u16 map, addr host, u16     room added
    sRoomList 03  u16 id, u8 mask, chosen fields of part one, u8 mask, chosen fields of part two         room changed
    sRoomList 02  u16 id                                                                                 room removed

    python tools/test_roomlist.py
"""
import struct
import sys

from test_client import check, first_channel, ws
from test_intrude import drain, player

import test_client
test_client.Client.skip_pushes = False  # this test is about those pushes


def main():
    ok = True
    watcher, _ = player("rw")
    watcher.send(bytes.fromhex("3204") + struct.pack("<H", first_channel()))
    drain(watcher)

    host, _ = player("rh")
    host.send(bytes.fromhex("3200") + ws("live list") + bytes([0, 0, 2, 6, 0, 0, 1, 1]))
    entered = next(reply for reply in drain(host) if reply[:2] == bytes([0x10, 0x05]))
    room = entered[3:5]

    seen = [reply for reply in drain(watcher, 2.5) if reply[:1] == bytes([0x0D])]
    added = next((reply for reply in seen if reply[1] == 1), b"")
    title = ws("live list")
    ok &= check("a new room is announced: sRoomList 01, id, title, options 0C (intrusion + observers, as asked), 1 of 6 players",
                added[2:4] == room and added[4:4 + len(title)] == title and added[4 + len(title):7 + len(title)] == bytes([0x0C, 1, 6]),
                str([reply.hex() for reply in seen]))

    # a room with a password is listed as private (option bit 0), and only opens with the password
    locked_host, _ = player("rp")
    locked_title = ws("locked")
    locked_host.send(bytes.fromhex("3200") + locked_title + struct.pack("<H", 4) + b"pass" + bytes([2, 6, 0, 0, 0, 1]))
    drain(locked_host)
    seen = [reply for reply in drain(watcher, 2.5) if reply[:2] == bytes([0x0D, 0x01])]
    locked = next((reply for reply in seen if reply[4:4 + len(locked_title)] == locked_title), b"")
    ok &= check("a room with a password is announced with option bit 0 set (private) and observers: 09",
                len(locked) > 4 + len(locked_title) and locked[4 + len(locked_title)] == 0x09, str([reply.hex() for reply in seen]))
    stranger, _ = player("rs")
    stranger.send(bytes.fromhex("3201") + locked[2:4] + struct.pack("<H", 0))
    refused = [reply.hex() for reply in drain(stranger)]
    ok &= check("joining it without the password is refused (sLobby 02)", any(reply.startswith("0f02") for reply in refused), str(refused))
    stranger.send(bytes.fromhex("3201") + locked[2:4] + struct.pack("<H", 4) + b"pass")
    entered = [reply.hex()[:6] for reply in drain(stranger)]
    ok &= check("joining it with the password works (sRoom 05)", any(reply.startswith("1005") for reply in entered), str(entered))
    stranger.sock.close()
    locked_host.sock.close()
    drain(watcher, 2.5)

    guest, _ = player("rg")
    guest.send(bytes.fromhex("3201") + room + bytes(3))
    drain(guest)
    seen = [reply for reply in drain(watcher, 2.5) if reply[:1] == b"\x0d"]
    first = seen[0] if seen else b""
    ok &= check("a second player joins: sRoomList 03 with only the player count (mask 04, count, mask 00)",
                first[:2] == bytes([0x0D, 3]) and first[2:4] == room and first[4] == 4 and first[5] >= 2 and first[6:] == bytes(1),
                str([reply.hex() for reply in seen]))

    guest.send(bytes.fromhex("3306")); drain(guest)
    host.send(bytes.fromhex("3306")); drain(host)
    seen = [reply for reply in drain(watcher, 3.5) if reply[:1] == b"\x0d"]
    ok &= check("the room closes: sRoomList 02 with its id comes last", bool(seen) and seen[-1] == b"\x0d\x02" + room,
                str([reply.hex() for reply in seen]))

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
