"""Leaderboard, as the game client and its Flash script read it.

    request   cRank 0C: u8 type (0 daily, 1 hall of fame), u8 mode (1 TDM, 2 Jessium, 3 FFA, 4 survival, 5 boss),
                        u32 first rank (from 1), u8, u32 rows
    reply     sRank 08: u32 time, u8 type, u8 mode, u8 cells,
                        {u32 line, u8 column (1 wins, 2 kills, 3 assists), wstr nick, u32 rank, u32 record, u32 previous rank}
    standing  cRank 0B -> sRank 07: u8 rows, {u8 type + 1, u8 mode, u8 column, u32 rank, u32 record, u32 previous rank}

    python tools/test_rank.py                 bots must not be listed (the default)
    EXPECT_BOTS=1 python tools/test_rank.py   bots must be listed (config: Bots.ShowOnLeaderboard = true)
"""
import os
import struct
import sys

from test_client import check
from test_intrude import drain, player


def board(client, kind, mode, first=1, count=50):
    client.send(bytes.fromhex("3d0c") + bytes([kind, mode]) + struct.pack("<IBI", first, 0, count))
    reply = next((r for r in drain(client) if r[:2] == b"\x1c\x08"), b"")
    if len(reply) < 9:
        return None, [], False
    _, got_kind, got_mode, cells = struct.unpack_from("<IBBB", reply, 2)
    parsed, at = [], 9
    for _ in range(cells):
        line, column, length = struct.unpack_from("<IBH", reply, at)
        at += 7
        name = reply[at:at + 2 * length].decode("utf-16-le")
        at += 2 * length
        parsed.append((line, column, name) + struct.unpack_from("<III", reply, at))
        at += 12
    return (got_kind, got_mode), parsed, at == len(reply)


def standing(client):
    client.send(bytes.fromhex("3d0b"))
    reply = next((r for r in drain(client) if r[:2] == b"\x1c\x07"), b"\x1c\x07\x00")
    return [struct.unpack_from("<BBBIII", reply, 3 + 15 * i) for i in range(reply[2])]


def main():
    expect_bots = os.environ.get("EXPECT_BOTS") == "1"
    ok = True
    c, uid = player("rk")
    c.send(bytes.fromhex("3119")); drain(c)

    boards = {}
    for kind in (0, 1):
        for mode in range(1, 6):
            header, cells, exact = board(c, kind, mode)
            ok &= check("%s, mode %d: header echoes type then mode, message parses to its end" % ("daily" if kind == 0 else "hall of fame", mode),
                        header == (kind, mode) and exact, str(header))
            boards[(kind, mode)] = cells
            for column in (1, 2, 3):
                mine = [cell for cell in cells if cell[1] == column]
                good = ([cell[3] for cell in mine] == list(range(1, len(mine) + 1)) and [cell[0] for cell in mine] == [cell[3] for cell in mine]
                        and [cell[4] for cell in mine] == sorted((cell[4] for cell in mine), reverse=True) and all(cell[4] > 0 for cell in mine))
                if not good:
                    ok &= check("  column %d: lines = ranks 1..n, records falling, nobody with 0" % column, False, str(mine[:3]))
    ok &= check("cells only use columns 1-3", {cell[1] for cells in boards.values() for cell in cells} <= {1, 2, 3})
    hall = boards[(1, 1)]
    ok &= check("hall of fame TDM has entries (the ladder test played scored matches)", len(hall) > 0, str(len(hall)))
    ok &= check("hall of fame: players ranked before today keep their rank (previous rank = rank), nobody is 'new' twice",
                all(cell[5] in (0, cell[3]) for cell in hall), str([cell for cell in hall if cell[5] not in (0, cell[3])][:2]))

    rows = standing(c)
    ok &= check("own standing: 2 types x 5 modes x 3 columns, types sent as 1 and 2",
                sorted(row[:3] for row in rows) == sorted((t, m, col) for t in (1, 2) for m in range(1, 6) for col in (1, 2, 3)), str(len(rows)))
    ok &= check("own standing: a new player is unranked everywhere (rank 0 shows N/A)", all(row[3:] == (0, 0, 0) for row in rows), str(rows[:1]))

    wins = [cell for cell in hall if cell[1] == 1]
    if len(wins) >= 2:
        _, paged, _ = board(c, 1, 1, 2, 1)
        got = [cell for cell in paged if cell[1] == 1]
        ok &= check("paging: first rank 2, one row gives exactly rank 2", [(cell[3], cell[2]) for cell in got] == [(2, wins[1][2])], str(got))

    listed = {cell[2] for cells in boards.values() for cell in cells}
    print("     hall of fame TDM wins:", ", ".join("%d. %s (%d)" % (cell[3], cell[2], cell[4]) for cell in wins[:5]) or "(empty)")
    daily = [cell for cell in boards[(0, 1)] if cell[1] == 1]
    print("     daily TDM wins:       ", ", ".join("%d. %s (%d)" % (cell[3], cell[2], cell[4]) for cell in daily[:5]) or "(empty)")
    bots = {"Mina", "Juno", "Tobi"} & listed
    if expect_bots:
        ok &= check("bots are listed", len(bots) > 0, str(sorted(bots)))
    else:
        ok &= check("bots are not listed", not bots, str(sorted(bots)))

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
