"""Player-record flags, daily missions and server notices.

    sUserInfo 00   second part (last 274 bytes): flags block at 226 (21 bytes: [1] game master,
                   [11] owns a Class Unlock ticket), daily missions at 247 (5 x {u16 id, u16 count} + u32 day)
    sUserInfo 01   flags field (mask B bit 6): u32 byte mask + bytes; missions field (bit 7): u16 24, 3 mask bytes, 24 bytes
    sNotice 06     u16 room - the room was closed by its host

    python tools/test_extras.py
"""
import struct
import sys
import time

from test_client import first_channel, Client, check, ws
from test_intrude import drain, has


def main():
    ok = True
    stamp = format(int(time.time() * 10) % 0xFFFFFF, "x")
    c = Client()
    c.finish_handshake(c.login("ex" + stamp, "extras12"))
    drain(c)
    c.send(bytes.fromhex("2d03") + ws("EX" + stamp))
    uid = next(reply[1:5] for reply in drain(c) if reply[:1] == b"\x06")
    c.send(bytes.fromhex("2d0401f6010400160000000000"))
    record = next((reply for reply in drain(c) if reply[:2] == b"\x09\x00"), b"")
    second = record[-274:]
    missions = [struct.unpack_from("<HH", second, 247 + 4 * i) for i in range(5)]
    day, = struct.unpack_from("<I", second, 267)
    ok &= check("full record: five daily missions with ids from the mission table and count 0",
                len(record) > 300 and all(0 < mission <= 60 and count == 0 for mission, count in missions) and len({m for m, _ in missions}) == 5, str(missions))
    ok &= check("full record: the mission day is today (UTC midnight)", day % 86400 == 0 and abs(time.time() - day) < 2 * 86400, str(day))
    ok &= check("full record: not a game master, no Class Unlock ticket yet", second[227] == 0 and second[237] == 0, second[226:247].hex())

    c.send(bytes.fromhex("2e00"))
    home = drain(c)
    flag = b"\x09\x01" + bytes(8) + struct.pack("<HI", 0x40, 1 << 11) + b"\x01"
    ok &= check("home: level rewards arrive, then the 'can unlock a class' flag is switched on", flag in home, str([r.hex()[:30] for r in home if r[:2] == b"\x09\x01"]))

    c.send(bytes.fromhex("3b0102")); drain(c)
    c.send(bytes.fromhex("3b0103")); drain(c)
    c.send(bytes.fromhex("3b0104")); drain(c)
    c.send(bytes.fromhex("3b0105"))
    last = drain(c)
    off = b"\x09\x01" + bytes(8) + struct.pack("<HI", 0x40, 1 << 11) + b"\x00"
    ok &= check("after the fourth unlock no ticket is left: the flag is switched off", off in last, str([r.hex()[:30] for r in last if r[:2] == b"\x09\x01"]))

    c.send(bytes.fromhex("3118")); drain(c)
    c.send(bytes.fromhex("3203") + struct.pack("<H", first_channel())); drain(c)
    c.send(bytes.fromhex("3200") + ws("extras") + bytes([0, 0, 1, 8, 0, 0, 1, 1]))
    room = next(reply for reply in drain(c) if reply[:2] == b"\x10\x05")[3:5]

    guest = Client()
    guest.finish_handshake(guest.login("eg" + stamp, "extras12"))
    drain(guest)
    guest.send(bytes.fromhex("2d03") + ws("EG" + stamp)); drain(guest)
    guest.send(bytes.fromhex("2d0401f6010400160000000000")); drain(guest)
    guest.send(bytes.fromhex("2e00")); drain(guest)
    guest.send(bytes.fromhex("3118")); drain(guest)
    guest.send(bytes.fromhex("3203") + struct.pack("<H", first_channel())); drain(guest)
    guest.send(bytes.fromhex("3201") + room + bytes(3))
    ok &= check("guest joins the room", has(drain(guest), b"\x10\x05"))
    drain(c)
    c.send(bytes.fromhex("3306")); drain(c)
    closed = drain(guest)
    ok &= check("host leaves: the guest gets sNotice 06 with the room id, then the lobby", has(closed, b"\x1a\x06" + room) and has(closed, b"\x0e\x03"),
                str([r.hex()[:10] for r in closed]))

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
