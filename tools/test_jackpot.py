"""Lucky box jackpot and the Jackpot ticket.

A lucky box (5501, opened with key 5997) has a jackpot on its rarest prize: that pull also drops
a Jackpot ticket (item 499). Using the ticket (cInventory 1B) pays a big prize - mostly BP,
sometimes an item at +8 or better.

    python tools/test_jackpot.py
"""
import struct
import sys

from test_client import check
from test_intrude import drain, has, player

BOX, KEY, TICKET = 5501, 5997, 499


def buy(client, item):
    client.send(bytes.fromhex("301e") + struct.pack("<I", item) + bytes(7) + bytes([1, 1]))
    for reply in drain(client, 0.15):
        if reply[:2] == b"\x0b\x01":
            return struct.unpack_from("<H", reply, 4)[0]
    return 0


def entries(message, start):
    return [(struct.unpack_from("<H", message, at)[0], struct.unpack_from("<I", message, at + 3)[0], struct.unpack_from("<H", message, at + 9)[0])
            for at in range(start, len(message) - 19, 20)]


def gold_of(replies):
    for reply in replies:
        if reply[:2] == b"\x09\x01" and reply[10:12] == b"\x02\x00":
            return struct.unpack_from("<I", reply, 12)[0]
    return None


def main():
    ok = True
    c, uid = player("jp")
    c.send(bytes.fromhex("311b")); drain(c)

    ticket_slot, opened = 0, 0
    while ticket_slot == 0 and opened < 150:
        box, key = buy(c, BOX), buy(c, KEY)
        c.send(bytes.fromhex("2f18") + struct.pack("<HH", box, key))
        opened += 1
        for reply in drain(c, 0.15):
            if reply[:2] == b"\x0b\x00":
                for slot, item, _ in entries(reply, 24):
                    if item == TICKET:
                        ticket_slot = slot
    ok &= check("a lucky box jackpot drops a Jackpot ticket (10 percent a box)", ticket_slot != 0, "after %d boxes" % opened)
    if not ticket_slot:
        print("SOME FAILED")
        return 1
    print("     jackpot after %d boxes" % opened)

    c.send(bytes.fromhex("3117")); drain(c)
    c.send(bytes.fromhex("311a")); drain(c)
    c.send(bytes.fromhex("2f11") + struct.pack("<H", 0)); before = drain(c)
    c.send(bytes.fromhex("2f1b") + struct.pack("<H", ticket_slot))
    used = drain(c)
    ok &= check("use the ticket: sInventory 05 + Success", has(used, b"\x0b\x05\x01"), str([r.hex()[:8] for r in used]))
    listing = next((r for r in used if r[:2] == b"\x0b\x00"), b"")
    ok &= check("the ticket is gone from the inventory", all(item != TICKET for _, item, _ in entries(listing, 24)))
    prize = next((r for r in used if r[:2] == b"\x0b\x01"), b"")
    message = next((r for r in used if r[:1] == b"\x17"), b"")
    if prize:
        level = struct.unpack_from("<H", prize, 15)[0] if len(prize) >= 17 else 0
        ok &= check("prize: an item at +8 or better (level value 18-20 or 38-40)", level in (18, 19, 20, 38, 39, 40), str(level))
    else:
        ok &= check("prize: BP (balance message after the use)", gold_of(used) is not None, str(gold_of(used)))
    ok &= check("the player is told what was won", b"J\x00a\x00c\x00k\x00p\x00o\x00t\x00" in message, message.hex()[:40])

    c.send(bytes.fromhex("2f1b") + struct.pack("<H", ticket_slot))
    ok &= check("using it again: refused", has(drain(c), b"\x0b\x05") and not has(drain(c), b"\x0b\x05\x01"))

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
