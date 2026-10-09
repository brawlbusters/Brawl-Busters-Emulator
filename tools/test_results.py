"""End of a match in every mode, in the order the game's host sends things.

    during the match   cHost 0C events: 01 kill (killer, assister, victim, revenge), 02 death without a killer,
                       03 charger used, 0A item used, 0E wave cleared (wave, stars), 0F/12 first slay,
                       10/13 last slay, 11/14 player died, 15 jessium score (red, blue)
    game over          cHost 13
    report             cHost 0C ev 04 (player, attack %, best combo, 2 bytes) and the list of the mode
                       (05 survival / 07 boss: slays, revives; 06 jessium), cHost 0D statistics, cHost 0A closed
    result screen      sMode 12, then sGame 03: u8 formal, u8 players,
                       {wstr nick, u8 level, u8 ladder level, u8 team, u32 titles, mode part}, mode tail, reward record
    back to the room   cCommand 03 -> sRoom 08, sMode 0C

    python tools/test_results.py
"""
import struct
import sys

from test_client import check, ws
from test_intrude import drain, has, player

FIRST_KILL, LAST_KILL, REVENGE, LONG_LIFE, IMMORTAL = 1, 2, 4, 8, 16
CROWN1, CROWN2, CROWN3 = 1 << 5, 1 << 6, 1 << 7
ITEM, CHARGER, COMBO, PERFECT = 1 << 8, 1 << 9, 1 << 10, 1 << 12
KILL_CROWN, ASSIST_CROWN, ATTACK_CROWN, SLAY_CROWN, REVIVE_CROWN, JESSIUM_CROWN = (1 << bit for bit in (16, 17, 18, 19, 20, 21))

PARTS = {1: "<BBB", 2: "<HBB", 4: "<BBBBB", 8: "<BBBB", 9: "<BBH"}


def statistics(*players):
    out = bytes([len(players)])
    for uid, kills, assists, mobs in players:
        block = bytearray(123)
        struct.pack_into("<BHBBH", block, 16, 1, 200, kills, assists, mobs)
        out += uid + bytes(block)
    return out


def start(mode, title, guests=()):
    host, host_id = player("rh")
    host.send(bytes.fromhex("3200") + ws(title) + bytes([0, 0, mode, 8 if mode in (1, 4, 8) else 4, 0, 0, 1, 1]))
    room = next(reply for reply in drain(host) if reply[:2] == b"\x10\x05")[3:5]
    others = []
    for tag in guests:
        guest, guest_id = player(tag)
        guest.send(bytes.fromhex("3201") + room + bytes(3)); drain(guest)
        guest.send(bytes.fromhex("330a01")); drain(guest); drain(host)
        others.append((guest, guest_id))
    host.send(bytes.fromhex("330c")); drain(host, 1.5)
    host.send(bytes.fromhex("330e"))
    game = next((reply for reply in drain(host, 1.5) if reply[:2] == b"\x15\x00"), b"")
    room4 = game[2:6]
    for guest, _ in others:
        drain(guest)
    host.send(bytes.fromhex("3709")); drain(host)
    for guest, _ in others:
        guest.send(bytes.fromhex("3709")); drain(guest, 1.2)
    for uid in [host_id] + [guest_id for _, guest_id in others]:
        host.send(b"\x36\x0c" + room4 + b"\x00" + uid)
    drain(host)
    for guest, _ in others:
        drain(guest)
    return host, host_id, others, room4


def finish(host, room4, events_after, stats):
    host.send(b"\x36\x13" + room4)
    quiet = drain(host)
    for event in events_after:
        host.send(b"\x36\x0c" + room4 + event)
    host.send(b"\x36\x0d" + room4 + stats)
    host.send(b"\x36\x0a" + room4)
    return quiet


def parse(result, mode):
    formal, count = result[2], result[3]
    at, rows = 4, []
    layout = PARTS[mode]
    for _ in range(count):
        length, = struct.unpack_from("<H", result, at)
        nick = result[at + 2:at + 2 + 2 * length].decode("utf-16le")
        at += 2 + 2 * length
        level, ladder, team, flags = struct.unpack_from("<BBBI", result, at)
        at += 7
        part = struct.unpack_from(layout, result, at)
        at += struct.calcsize(layout)
        rows.append(dict(nick=nick, team=team, flags=flags, part=part))
    return formal, rows, at


def result_of(client, name, ok):
    replies = drain(client, 1.0)
    modes = [i for i, reply in enumerate(replies) if reply == b"\x0e\x12"]
    results = [i for i, reply in enumerate(replies) if reply[:2] == b"\x15\x03"]
    good = bool(modes) and bool(results) and modes[0] < results[0]
    ok &= check(name + ": sMode 12 (result screen), then sGame 03", good, str([reply.hex()[:6] for reply in replies]))
    ok &= check(name + ": not sent to the waiting room before the result screen is closed", not has(replies, b"\x0e\x0c"))
    return (replies[results[0]] if results else b"\x15\x03\x00\x00"), ok


def back(client, name, ok):
    client.send(bytes.fromhex("3b03"))
    replies = drain(client)
    ok &= check(name + ": cCommand 03 -> room state and sMode 0C", has(replies, b"\x10\x08") and has(replies, b"\x0e\x0c"),
                str([reply.hex()[:6] for reply in replies]))
    return ok


def sheet_of(client):
    client.send(bytes.fromhex("3306")); drain(client)
    client.send(bytes.fromhex("3117")); drain(client)
    client.send(bytes.fromhex("3e01"))
    return next((reply for reply in drain(client) if reply[:2] == b"\x13\x09"), bytes(900))[2:]


def team_deathmatch(ok):
    host, a, others, room4 = start(1, "results tdm", ["rg"])
    guest, b = others[0]
    kill = lambda killer, assister, victim, revenge=0: b"\x01" + killer + assister + victim + bytes([revenge])
    none = bytes(4)
    for event in (kill(a, none, b), kill(b, none, a, 1), kill(a, none, b, 1), kill(a, none, b), b"\x0a" + a + struct.pack("<H", 2),
                  b"\x03" + b, b"\x0b" + a + b"\x17"):
        host.send(b"\x36\x0c" + room4 + event)
    ok &= check("TDM: live events are accepted without a reply", drain(host) == [])
    common = b"\x04\x02" + a + bytes([70]) + struct.pack("<Hbb", 12, 0, 0) + b + bytes([30]) + struct.pack("<Hbb", 4, 0, 0)
    quiet = finish(host, room4, [common], statistics((a, 3, 0, 0), (b, 1, 0, 0)))
    ok &= check("TDM: cHost 13 alone does not end anything for the players", not has(quiet, b"\x0e") and not has(quiet, b"\x15\x03"))

    expect = {a: ((3, 0, 70), FIRST_KILL | LAST_KILL | REVENGE | LONG_LIFE | CROWN2 | KILL_CROWN | ATTACK_CROWN | ITEM | COMBO),
              b: ((1, 0, 30), REVENGE | CHARGER)}
    for name, client, uid in (("TDM host", host, a), ("TDM guest", guest, b)):
        result, ok = result_of(client, name, ok)
        formal, rows, at = parse(result, 1)
        ok &= check(name + ": formal game with two rows", (formal, len(rows)) == (1, 2), str((formal, len(rows))))
        got = sorted((row["part"], row["flags"]) for row in rows)
        ok &= check(name + ": rows are kills, assists, attack share with the titles of each player", got == sorted(expect.values()),
                    str([(part, hex(flags)) for part, flags in got]))
        ok &= check(name + ": team totals 3 - 1", sorted(result[at:at + 2]) == [1, 3], result[at:at + 2].hex())
        ok &= check(name + ": 34-byte reward record", len(result) - at - 2 == 34, str(len(result) - at - 2))
        ok = back(client, name, ok)

    sheet = sheet_of(host)
    block = struct.unpack_from("<8I", sheet, 0)
    ok &= check("My Records, class 1 (the statistics block of cHost 0D): play time 200, kills 3", (block[0], block[4]) == (200, 3), str(block))
    tdm = 268
    sums = struct.unpack_from("<3I", sheet, tdm + 0x14)
    titles = struct.unpack_from("<14h", sheet, tdm + 0x20)
    ok &= check("My Records TDM: totals 3 kills, 0 assists, 70 attack", sums == (3, 0, 70), str(sums))
    ok &= check("My Records TDM: titles kill crown, attack crown, two crowns, first kill, last kill, revenge, combo, item",
                titles == (1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 1, 0, 1), str(titles))
    best = sheet[570 + 2 + 60 + 4:570 + 2 + 60 + 9]
    ok &= check("My Records TDM: streak 0 (two players pay nothing but count), best kills 3 / attack 70", tuple(best[2:5]) == (3, 0, 70), best.hex())
    return ok


def survival(ok):
    host, a, _, room4 = start(2, "results suv")
    for event in (b"\x0f" + a, b"\x0e\x01\x04", b"\x0e\x02\x03", b"\x11" + a, b"\x10" + a):
        host.send(b"\x36\x0c" + room4 + event)
    drain(host)
    common = b"\x04\x01" + a + bytes([100]) + struct.pack("<Hbb", 9, 0, 0)
    finish(host, room4, [common, b"\x05\x01" + a + struct.pack("<HB", 37, 2)], statistics((a, 0, 0, 37)))
    result, ok = result_of(host, "survival", ok)
    formal, rows, at = parse(result, 2)
    ok &= check("survival: row is slays 37, attack 100, revives 2", rows and rows[0]["part"] == (37, 100, 2), str(rows))
    ok &= check("survival: titles first slay, last slay, three crowns, combo (died once, so not immortal)",
                rows and rows[0]["flags"] == FIRST_KILL | LAST_KILL | CROWN3 | SLAY_CROWN | REVIVE_CROWN | ATTACK_CROWN | COMBO, hex(rows[0]["flags"]) if rows else "")
    success, seconds, last, most, stars = struct.unpack_from("<BfBBB", result, at)
    ok &= check("survival: tail is failed at wave 2 of the map's waves with 7 stars", (success, last, stars) == (0, 2, 7) and most >= 5, str((success, seconds, last, most, stars)))
    ok &= check("survival: 33-byte reward record", len(result) - at - 8 == 33, str(len(result) - at - 8))
    ok = back(host, "survival", ok)
    return ok


def boss(ok):
    host, a, _, room4 = start(9, "results bsr")
    for event in (b"\x12" + a, b"\x13" + a):
        host.send(b"\x36\x0c" + room4 + event)
    drain(host)
    common = b"\x04\x01" + a + bytes([100]) + struct.pack("<Hbb", 20, 0, 0)
    finish(host, room4, [common, b"\x07\x01" + a + struct.pack("<HB", 14, 0)], statistics((a, 0, 0, 14)))
    result, ok = result_of(host, "boss battle", ok)
    formal, rows, at = parse(result, 9)
    ok &= check("boss battle: row is attack 100, revives 0, slays 14", rows and rows[0]["part"] == (100, 0, 14), str(rows))
    ok &= check("boss battle: immortal, first and last slay, two crowns, perfect",
                rows and rows[0]["flags"] == FIRST_KILL | LAST_KILL | IMMORTAL | CROWN2 | SLAY_CROWN | ATTACK_CROWN | COMBO | PERFECT, hex(rows[0]["flags"]) if rows else "")
    grade, seconds = struct.unpack_from("<Bf", result, at)
    name_length, = struct.unpack_from("<H", result, at + 5)
    name = result[at + 7:at + 7 + name_length].decode("latin1")
    ok &= check("boss battle: cleared with the best grade (5), boss name is the map's name", grade == 5 and name.startswith("BSR_"), str((grade, seconds, name)))
    ok &= check("boss battle: 33-byte reward record", len(result) - at - 7 - name_length == 33, str(len(result) - at - 7 - name_length))
    ok = back(host, "boss battle", ok)

    sheet = sheet_of(host)
    table = 570
    count, = struct.unpack_from("<H", sheet, table)
    total = table + 2 + 1 + 64
    rounds, s_grade = struct.unpack_from("<I", sheet, total + 2)[0], struct.unpack_from("<4I", sheet, total + 6)
    sums = struct.unpack_from("<3I", sheet, total + 0x16)
    ok &= check("My Records boss: one round, cleared once with the top grade, totals 14 slays / 0 revives / 100 attack",
                (count, rounds, s_grade, sums) == (1, 1, (0, 0, 0, 1), (14, 0, 100)), str((count, rounds, s_grade, sums)))
    return ok


def free_for_all(ok):
    host, a, others, room4 = start(8, "results ffa", ["rf"])
    guest, b = others[0]
    none = bytes(4)
    for event in (b"\x01" + b + none + a + b"\x00", b"\x01" + b + none + a + b"\x00", b"\x02" + none + b):
        host.send(b"\x36\x0c" + room4 + event)
    drain(host)
    common = b"\x04\x02" + a + bytes([40]) + struct.pack("<Hbb", 0, 0, 0) + b + bytes([60]) + struct.pack("<Hbb", 0, 0, 0)
    finish(host, room4, [common], statistics((a, 0, 0, 0), (b, 2, 0, 0)))
    result, ok = result_of(guest, "free for all", ok)
    formal, rows, at = parse(result, 8)
    by_part = sorted(row["part"] for row in rows)
    ok &= check("free for all: rows are kills, assists, attack, place (0 is first)", by_part == [(0, 0, 40, 1), (2, 0, 60, 0)], str(by_part))
    ok &= check("free for all: no tail, 34-byte reward record", len(result) - at == 34, str(len(result) - at))
    ok = back(guest, "free for all", ok)
    drain(host)
    sheet = sheet_of(guest)
    places = struct.unpack_from("<8I", sheet, 490 + 4)
    extra = struct.unpack_from("<3h", sheet, 490 + 0x4A)
    ok &= check("My Records free for all: one first place; first place without an assist counted", places[0] == 1 and extra[0] == 1, str((places, extra)))
    return ok


def jessium(ok):
    host, a, others, room4 = start(4, "results jes", ["rj"])
    guest, b = others[0]
    for event in (b"\x15\x03\x01", b"\x15\x05\x02"):
        host.send(b"\x36\x0c" + room4 + event)
    drain(host)
    common = b"\x04\x02" + a + bytes([55]) + struct.pack("<Hbb", 0, 0, 0) + b + bytes([45]) + struct.pack("<Hbb", 0, 0, 0)
    jes = b"\x06\x02" + a + bytes([5, 1]) + b + bytes([2, 0])
    finish(host, room4, [common, jes], statistics((a, 0, 0, 0), (b, 0, 0, 0)))
    result, ok = result_of(host, "jessium", ok)
    formal, rows, at = parse(result, 4)
    by_part = sorted(row["part"] for row in rows)
    ok &= check("jessium: rows are kills, assists, jessium, 4th value, attack", by_part == [(0, 0, 2, 0, 45), (0, 0, 5, 1, 55)], str(by_part))
    ok &= check("jessium: tail is the last score event, 5 - 2", tuple(result[at:at + 2]) == (5, 2), result[at:at + 2].hex())
    ok = back(host, "jessium", ok)
    return ok


def main():
    ok = True
    for run in (team_deathmatch, survival, boss, free_for_all, jessium):
        ok = run(ok)
    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
