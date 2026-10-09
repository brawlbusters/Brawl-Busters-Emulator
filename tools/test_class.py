"""Class Unlock tickets (level rewards) and changing class.

    level rewards   a level-30 account receives the rewards of levels 2-30 at its first visit to the home
                    screen - among them four Class Unlock tickets (item 500, levels 2, 4, 7 and 11)
    unlock          cCommand 01: u8 class  -> sUserInfo 01 (profile field, owned-class mask) + sInventory 00
    change class    cUserInfo 03: u8 class -> sUserInfo 01 (current class)

    python tools/test_class.py
"""
import struct
import sys

from test_client import check
from test_intrude import drain, has, player

CLASS_UNLOCK = 500


def tickets(listing):
    count = 0
    for at in range(24, len(listing) - 19, 20):
        if struct.unpack_from("<I", listing, at + 3)[0] == CLASS_UNLOCK:
            count += struct.unpack_from("<H", listing, at + 13)[0]
    return count


def main():
    ok = True
    c, uid = player("cl")
    c.send(bytes.fromhex("311a"))
    listing = next((r for r in drain(c) if r[:2] == b"\x0b\x00"), b"")
    c.send(bytes.fromhex("3117")); drain(c)
    if not listing:
        c.send(bytes.fromhex("311b"))
        listing = next((r for r in drain(c) if r[:2] == b"\x0b\x00"), b"")
        c.send(bytes.fromhex("3117")); drain(c)
    ok &= check("level rewards: a level-30 account owns 4 Class Unlock tickets", tickets(listing) == 4, str(tickets(listing)))

    c.send(bytes.fromhex("2c0302"))
    reply = next((r for r in drain(c) if r[:2] == b"\x09\x01"), b"")
    ok &= check("change to a class that is still locked: refused, current class stays 1",
                reply == b"\x09\x01" + struct.pack("<Q", 4) + b"\x01" + b"\x00\x00", reply.hex())

    c.send(bytes.fromhex("3b0103"))
    got = drain(c)
    profile = next((r for r in got if r[:2] == b"\x09\x01"), b"")
    ok &= check("unlock class 3: owned-class mask becomes classes 1 and 3 (0x0A)",
                profile == b"\x09\x01" + struct.pack("<Q", 0x20) + b"\x04\x0a" + b"\x00\x00", profile.hex())
    after = next((r for r in got if r[:2] == b"\x0b\x00"), b"")
    ok &= check("unlock class 3: one ticket is used up (3 left)", tickets(after) == 3, str(tickets(after)))

    c.send(bytes.fromhex("3b0103"))
    ok &= check("unlock the same class again: refused, no ticket used", has(drain(c), b"\x0b\x05"))

    c.send(bytes.fromhex("2c0303"))
    reply = next((r for r in drain(c) if r[:2] == b"\x09\x01"), b"")
    ok &= check("change to class 3: current class is now 3", reply == b"\x09\x01" + struct.pack("<Q", 4) + b"\x03" + b"\x00\x00", reply.hex())

    c.send(bytes.fromhex("2c0301"))
    reply = next((r for r in drain(c) if r[:2] == b"\x09\x01"), b"")
    ok &= check("change back to class 1: still owned", reply[10:11] == b"\x01", reply.hex())

    def changers(c):
        c.send(bytes.fromhex("311b"))
        listing = next((r for r in drain(c) if r[:2] == b"\x0b\x00"), b"")
        c.send(bytes.fromhex("3117")); drain(c)
        return sum(struct.unpack_from("<H", listing, at + 13)[0] for at in range(24, len(listing) - 19, 20) if listing[at + 2] == 23)

    c.send(bytes.fromhex("311b")); drain(c)
    c.send(bytes.fromhex("301e") + struct.pack("<I", 300) + bytes(7) + bytes([1, 1])); drain(c)
    c.send(bytes.fromhex("3117")); drain(c)
    before = changers(c)
    c.send(bytes.fromhex("380a17"))
    c.send(bytes.fromhex("380a17"))
    ok &= check("Slot Changer use (cItem 0A, type 23) gets no reply", drain(c) == [])
    ok &= check("bought 50 Slot Changers; two uses leave two fewer", before >= 50 and changers(c) == before - 2, "%d -> %d" % (before, changers(c)))

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
