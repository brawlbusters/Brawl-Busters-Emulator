"""Ladder (ranked): the ladder screen, ladder rooms and matchmaking.

    python tools/test_ladder.py
"""
import struct
import sys
import time

from test_client import check
from test_intrude import drain, has, player

# sUserInfo 01, part-two field bit 9: the "matchmaking in progress" byte of the own record - what shows the
# timer and the CANCEL button on the ranked screen.
MATCHING_ON = b"\x09\x01" + bytes(8) + b"\x00\x02\x01"
MATCHING_OFF = b"\x09\x01" + bytes(8) + b"\x00\x02\x00"


def room_state(message, offset):
    title_end = offset + 2 + 2 * struct.unpack_from("<H", message, offset)[0]
    return message[title_end + 3]


def main():
    ok = True
    a, a_id = player("la")
    b, b_id = player("lb")

    a.send(bytes.fromhex("311e"))
    opened = drain(a)
    data = next((reply for reply in opened if reply[:2] == b"\x11\x00"), b"")
    ok &= check("open ladder: sMode 0B + sLadder 00 (4 + 14 + 24 bytes)", has(opened, b"\x0e\x0b") and len(data) == 2 + 42, data.hex())
    ok &= check("the table's update time is not in the future", len(data) > 6 and 0 < struct.unpack_from("<I", data, 2)[0] <= time.time() + 5)

    a.send(bytes.fromhex("340501"))
    created = drain(a)
    entered = next((reply for reply in created if reply[:2] == b"\x10\x05"), b"")
    ok &= check("create ladder room: sRoom 05 with room type 2 (multi) + sMode 0E", entered[2:3] == b"\x02" and has(created, b"\x0e\x0e"),
                str([r.hex()[:8] for r in created]))
    title_end = 5 + 2 + 2 * struct.unpack_from("<H", entered, 5)[0]
    ok &= check("ladder room info has no map part: host id follows the four room bytes", entered[title_end + 4:title_end + 8] == a_id,
                entered[title_end:title_end + 10].hex())

    a.send(bytes.fromhex("3312"))
    finding = drain(a)
    update = next((reply for reply in finding if reply[:2] == b"\x10\x08"), b"")
    ok &= check("find match: room state 6 (matching) + matchmaking flag on", bool(update) and room_state(update, 3) == 6 and MATCHING_ON in finding,
                str([r.hex()[:12] for r in finding]))
    a.send(bytes.fromhex("3313"))
    cancelled = drain(a)
    update = next((reply for reply in cancelled if reply[:2] == b"\x10\x08"), b"")
    ok &= check("cancel find match: room state 0 + matchmaking flag off", bool(update) and room_state(update, 3) == 0 and MATCHING_OFF in cancelled)

    a.send(bytes.fromhex("3306"))
    ok &= check("exit ladder room: back on the ladder screen (sMode 0B)", has(drain(a), b"\x0e\x0b"))

    b.send(bytes.fromhex("311e")); drain(b)
    a.send(bytes.fromhex("3403"))
    ok &= check("start match alone: matchmaking flag on, nothing else", drain(a) == [MATCHING_ON])
    a.send(bytes.fromhex("3404"))
    ok &= check("cancel match: matchmaking flag off", MATCHING_OFF in drain(a))

    a.send(bytes.fromhex("3403")); drain(a)
    b.send(bytes.fromhex("3403"))
    time.sleep(3)
    for name, client, own, other in (("first", a, a_id, b_id), ("second", b, b_id, a_id)):
        got = drain(client)
        entered = next((reply for reply in got if reply[:2] == b"\x10\x05"), b"")
        game = next((reply for reply in got if reply[:2] == b"\x15\x00"), b"")
        ok &= check(name + " player: matched into a ladder room of type 1 (single) + sMode 0D",
                    entered[2:3] == b"\x01" and has(got, b"\x0e\x0d") and other in entered, str([r.hex()[:8] for r in got]))
        ok &= check(name + " player: the match starts (sGame 00 with the first player as host, sMode 0F)",
                    game[6:10] == a_id and has(got, b"\x0e\x0f"), game.hex()[:40])

    a.send(bytes.fromhex("3709"))
    loaded = drain(a)
    hosts = next((reply for reply in loaded if reply[:2] == b"\x14\x00"), b"")
    ok &= check("host loaded: sHost 00 lists both players", a_id in hosts and b_id in hosts and has(loaded, b"\x0e\x10"))
    first, second = hosts.find(a_id), hosts.find(b_id)
    team = lambda at: hosts[at + 4 + 2 + 2 * struct.unpack_from("<H", hosts, at + 4)[0] + 2]
    ok &= check("the two players are on opposite teams", first > 0 and second > 0 and {team(first), team(second)} == {0, 1})

    room4 = hosts[2:6]
    b.send(bytes.fromhex("3709")); drain(b, 1.2)
    for uid in (a_id, b_id):
        a.send(b"\x36\x0c" + room4 + b"\x00" + uid)
    drain(a), drain(b)
    kill = lambda killer, victim: b"\x36\x0c" + room4 + b"\x01" + killer + bytes(4) + victim + b"\x00"
    for killer, victim, times in ((a_id, b_id, 7), (b_id, a_id, 3)):
        for _ in range(times):
            a.send(kill(killer, victim))
    a.send(b"\x36\x13" + room4)
    share = lambda uid, attack: uid + bytes([attack]) + struct.pack("<Hbb", 0, 0, 0)
    a.send(b"\x36\x0c" + room4 + b"\x04\x02" + share(a_id, 70) + share(b_id, 30))
    a.send(b"\x36\x0a" + room4)
    for name, client in (("winner", a), ("loser", b)):
        replies = drain(client, 1.0)
        result = next((reply for reply in replies if reply[:2] == b"\x15\x03"), b"")
        ok &= check(name + ": result screen (sMode 12, sGame 03) with two ladder rows", has(replies, b"\x0e\x12") and result[2:4] == b"\x01\x02", result.hex()[:12])
        at, rows = 4, []
        for _ in range(2 if result else 0):
            at += 2 + 2 * struct.unpack_from("<H", result, at)[0] + 7
            rows.append(struct.unpack_from("<BBBB", result, at))
            at += 4
        ok &= check(name + ": ladder rows are kills, assists, attack, deaths; then team kills and a 34-byte reward record",
                    sorted(rows) == [(3, 0, 30, 7), (7, 0, 70, 3)] and sorted(result[at:at + 2]) == [3, 7] and len(result) - at - 2 == 34, str(rows))
        client.send(bytes.fromhex("3b03"))
        ok &= check(name + ": result closed -> back in the ladder room (sMode 0D)", has(drain(client), b"\x0e\x0d"))
    for name, client, expected in (("winner", a, (0, 1, 1, 0, 0, 7, 20)), ("loser", b, (0, 1, 0, 1, 0, 3, 0))):
        client.send(bytes.fromhex("3306")); drain(client)
        client.send(bytes.fromhex("311e"))
        data = next((reply for reply in drain(client) if reply[:2] == b"\x11\x00"), bytes(44))
        got = (struct.unpack_from("<H", data, 6)[0],) + struct.unpack_from("<6I", data, 20)
        table = struct.unpack_from("<7H", data, 6)
        ok &= check(name + ": ladder data shows table[0] = 0, matches, wins, losses, draws, score, points", got == expected, str(got))
        ok &= check(name + ": the seven rank boundaries start at 0 and rise", table[0] == 0 and all(x < y for x, y in zip(table, table[1:])), str(table))

    for name, client, expected in (("winner", a, (1, 0, 0)), ("loser", b, (0, 1, 0))):
        client.send(bytes.fromhex("3e01"))
        sheet = next((reply for reply in drain(client) if reply[:2] == b"\x13\x09"), bytes(672))
        got = tuple(struct.unpack_from("<I", sheet, 2 + 268 + at)[0] for at in (4, 10, 16))
        ok &= check(name + ": My Records sheet is 670 bytes with TDM wins, losses, draws", len(sheet) == 672 and got == expected, str(got))
        crowns = struct.unpack_from("<3h", sheet, 2 + 268 + 0x26)
        kills = sum(struct.unpack_from("<I", sheet, 2 + index * 0x20 + 0x10)[0] for index in range(5))
        ok &= check(name + ": two column crowns (kills, attack) for the winner, none for the loser; kills in the class block",
                    crowns == ((0, 1, 0) if name == "winner" else (0, 0, 0)) and kills == (7 if name == "winner" else 3), str((crowns, kills)))

    for client in (a, b):
        client.sock.close()
    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
