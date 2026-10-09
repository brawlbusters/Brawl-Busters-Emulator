"""Boss battle (mode 9), every message of one run, the way the game's host sends them.

    room      cLobby 00 (mode 9) -> sRoom 05, sRoom 08, sMode 0C
    start     cRoom 0C, cRoom 0E -> sGame 00 (a boss map), sMode 0F
    load      cGame 09           -> sHost 00, sMode 10
    enter     cHost 0C ev 00     -> sMode 11
    events    cHost 0C ev 12 first slay, 13 last slay (the boss is down), 14 player died
    over      cHost 13
    report    cHost 0C ev 04 (attack share, combo), ev 07 (count, {player, u16 slays, u8 revives}), cHost 0D, cHost 0A
    result    sMode 12, sGame 03: rows {.., u8 attack, u8 revives, u16 slays}, u8 grade (1 failed, 2-5 cleared), f32 seconds, str boss
    close     cCommand 03        -> sRoom 08, sMode 0C (back in the waiting room)

    python tools/test_boss.py
"""
import struct
import sys

from test_client import first_channel, check, ws
from test_intrude import drain, has, player


def statistics(uid, mob_kills):
    block = bytearray(123)
    struct.pack_into("<BHBBH", block, 16, 1, 60, 0, 0, mob_kills)
    return bytes([1]) + uid + bytes(block)


def run(c, uid, nick, cleared, ok):
    c.send(bytes.fromhex("3200") + ws("boss test") + bytes([0, 0, 9, 4, 0, 0, 1, 1]))
    created = drain(c)
    ok &= check("create boss room: sRoom 05 + sMode 0C", has(created, b"\x10\x05") and has(created, b"\x0e\x0c"), str([r.hex()[:8] for r in created]))
    c.send(bytes.fromhex("330c")); drain(c)
    c.send(bytes.fromhex("330e"))
    started = drain(c)
    game = next((reply for reply in started if reply[:2] == b"\x15\x00"), b"")
    ok &= check("start: sGame 00 + sMode 0F", bool(game) and has(started, b"\x0e\x0f"))
    room = game[2:6]
    c.send(bytes.fromhex("3709"))
    loaded = drain(c)
    ok &= check("loaded: sHost 00 + sMode 10", has(loaded, b"\x14\x00") and has(loaded, b"\x0e\x10"))
    c.send(b"\x36\x0c" + room + b"\x00" + uid)
    ok &= check("entered: sMode 11", has(drain(c), b"\x0e\x11"))

    for event in ((0x12, 0x13, 0x14) if cleared else (0x12, 0x14)):
        c.send(b"\x36\x0c" + room + bytes([event]) + uid)
    ok &= check("boss events are accepted without a reply", drain(c) == [])

    c.send(b"\x36\x13" + room)
    ok &= check("game over (cHost 13): nothing is sent yet", drain(c) == [])
    c.send(b"\x36\x0c" + room + b"\x04\x01" + uid + bytes([100]) + struct.pack("<Hbb", 6, 0, 0))
    c.send(b"\x36\x0c" + room + b"\x07\x01" + uid + struct.pack("<HB", 42, 1))
    c.send(bytes([0x36, 0x0D]) + room + statistics(uid, 42))
    c.send(b"\x36\x0a" + room)
    replies = drain(c, 1.0)
    result = next((reply for reply in replies if reply[:2] == b"\x15\x03"), b"")
    ok &= check("closed: sMode 12 and sGame 03 (result screen)", has(replies, b"\x0e\x12") and bool(result), str([r.hex()[:6] for r in replies]))
    at = 4 + 2 + 2 * struct.unpack_from("<H", result, 4)[0] + 7 if result else 0
    row = struct.unpack_from("<BBH", result, at) if result else ()
    grade = result[at + 4] if result else 0
    ok &= check("result row: attack 100, revives 1, slays 42", row == (100, 1, 42), str(row))
    ok &= check("result grade: %s" % ("cleared (2-5)" if cleared else "failed (1)"), (2 <= grade <= 5) if cleared else grade == 1, str(grade))
    c.send(bytes.fromhex("3b03"))
    ok &= check("result closed (cCommand 03): back in the waiting room (sMode 0C)", has(drain(c), b"\x0e\x0c"))
    c.send(bytes.fromhex("3306")); drain(c)
    return ok


def main():
    ok = True
    c, uid = player("bs")
    c.send(bytes.fromhex("3118")); drain(c)
    c.send(bytes.fromhex("3203") + struct.pack("<H", first_channel())); drain(c)
    nick = "BS"
    ok = run(c, uid, nick, True, ok)
    ok = run(c, uid, nick, False, ok)

    c.send(bytes.fromhex("3e01"))
    sheet = next((reply for reply in drain(c) if reply[:2] == bytes([0x13, 9])), bytes(900))[2:]
    table = 570
    total = table + 2 + 1 + 64
    got = (struct.unpack_from("<H", sheet, table)[0], struct.unpack_from("<I", sheet, total + 2)[0],
           sum(struct.unpack_from("<4I", sheet, total + 6)), struct.unpack_from("<3I", sheet, total + 0x16), struct.unpack_from("<I", sheet, 0x18)[0])
    ok &= check("My Records: 2 boss rounds, 1 clear, totals 84 slays / 2 revives / 200 attack; class 1 has the 84 slays",
                got == (1, 2, 1, (84, 2, 200), 84), str(got))
    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
