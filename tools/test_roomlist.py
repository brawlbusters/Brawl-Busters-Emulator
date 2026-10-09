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
    entered = next(reply for reply in drain(host) if reply[:2] == b"\x10\x05")
    room = entered[3:5]

    seen = [reply for reply in drain(watcher, 2.5) if reply[:1] == b"\x0d"]
    added = next((reply for reply in seen if reply[1] == 1), b"")
    title = ws("live list")
    ok &= check("a new room is announced: sRoomList 01, id, title, kind 8, 1 of 6 players",
                added[2:4] == room and added[4:4 + len(title)] == title and added[4 + len(title):7 + len(title)] == bytes([8, 1, 6]),
                str([reply.hex() for reply in seen]))

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
