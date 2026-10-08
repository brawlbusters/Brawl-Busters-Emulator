import struct
import sys

from test_client import check, ws
from test_shop import new_player_at_home

ITEM = 18


def gold_of(message):
    assert message[:12] == bytes.fromhex("0901") + bytes(8) + bytes.fromhex("0200"), message.hex()
    return struct.unpack_from("<I", message, 12)[0]


def items_of(message):
    assert message[:2] == bytes.fromhex("0b00"), message.hex()[:8]
    result = {}
    for offset in range(24, len(message), 2 + ITEM):
        slot, kind, item_id, opt2, opt3, opt4, quantity, state, expiry = struct.unpack_from("<HBIHHHHBI", message, offset)
        result[slot] = (item_id, opt3, quantity, expiry)
    return result


def buy(c, catalog_id, option=1):
    c.send(bytes.fromhex("301e") + struct.pack("<I", catalog_id) + bytes(7) + bytes([option, 1]))
    result, gold, added = c.recv(), c.recv(), c.recv()
    assert result == bytes.fromhex("0c1c00"), "purchase of %d refused: %s" % (catalog_id, result.hex())
    return gold_of(gold), struct.unpack_from("<H", added, 4)[0]


def main():
    ok = True
    c = new_player_at_home()
    c.send(bytes.fromhex("311b"))
    c.recv(); c.recv()

    gold, weapon = buy(c, 60012)
    gold, stone = buy(c, 251)
    gold, stone2 = buy(c, 251)

    c.send(bytes.fromhex("2f13") + struct.pack("<HH", weapon, stone))
    reply, _, listing = c.recv(), c.recv(), c.recv()
    items = items_of(listing)
    ok &= check("reinforce +0: reply 08 = ok, outcome 0, item slot, stone slot, level 11",
                reply == bytes.fromhex("0b0801") + bytes([0]) + struct.pack("<HHH", weapon, stone, 11), reply.hex())
    ok &= check("reinforce +0: the weapon is now 11 and the stone is used up",
                items.get(weapon, (0, 0))[1] == 11 and stone not in items, str(items))

    c.send(bytes.fromhex("2f13") + struct.pack("<HH", weapon, weapon))
    reply = c.recv()
    ok &= check("reinforce with a wrong item: reply 08 = not ok", reply == bytes.fromhex("0b0800"), reply.hex())

    c.send(bytes.fromhex("2f11") + struct.pack("<H", stone2))
    reply, balance, listing = c.recv(), c.recv(), c.recv()
    ok &= check("sell: reply 04 = ok, slot, 01", reply == bytes.fromhex("0b0401") + struct.pack("<H", stone2) + b"\x01", reply.hex())
    ok &= check("sell: +35 BP and the item is gone", gold_of(balance) == gold + 35 and stone2 not in items_of(listing), str(gold_of(balance) - gold))
    gold += 35

    gold, timed = buy(c, 501)
    c.send(bytes.fromhex("311a")); c.recv(); c.recv()
    c.send(bytes.fromhex("2f15") + struct.pack("<HBB", timed, 1, 1))
    reply, balance, listing = c.recv(), c.recv(), c.recv()
    before_after = items_of(listing)[timed][3]
    ok &= check("extend: reply 0A = ok, slot, option, new expiry",
                reply[:6] == bytes.fromhex("0b0a01") + struct.pack("<HB", timed, 1) and struct.unpack_from("<I", reply, 6)[0] == before_after, reply.hex())
    ok &= check("extend: 1240 BP paid", gold_of(balance) == gold - 1240, str(gold - gold_of(balance)))

    c.send(bytes.fromhex("2f19") + ws("Zq%d" % (gold % 100000)))
    reply = c.recv()
    ok &= check("nickname check, free name: 0C 01 (Success)", reply == bytes.fromhex("0b0c01"), reply.hex())
    c.send(bytes.fromhex("2f19") + ws("mik2"))
    reply = c.recv()
    ok &= check("nickname check, taken name: 0C 20 (Nick_AlreadyExist)", reply == bytes.fromhex("0b0c20"), reply.hex())

    c.send(bytes.fromhex("3118"))
    for _ in range(4):
        c.recv()
    c.send(bytes.fromhex("3203") + struct.pack("<H", 1))
    for _ in range(3):
        c.recv()
    c.send(bytes.fromhex("3205") + struct.pack("<H", 60000))
    reply = c.recv()
    ok &= check("room details of a missing room: sLobby 02 + Lobby_NotExistRoomInfo (105)", reply == bytes.fromhex("0f0269"), reply.hex())

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
