"""What the server refuses to believe from a client (MatchGuard, settings section AntiCheat).

A changed client can send any packet. Prices, balances and rewards never come from the client, but the outcome of
play does: single play runs on the player's computer, a match on its host's. This test plays the cheat's part and
checks that nothing is paid for it. The refusals are read from the server log ("Not plausible ...").

    python tools/test_anticheat.py      (the server must run with AntiCheat.Enabled, which is the default)
"""
import datetime
import glob
import os
import struct
import sys
import time

from test_client import check
from test_intrude import drain, has, player
from test_results import finish, start, statistics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def log_path():
    return sorted(glob.glob(os.path.join(ROOT, "logs", "Server-*.log")))[-1]


def log_since(offset):
    time.sleep(0.4)
    with open(log_path(), encoding="utf-8", errors="replace") as handle:
        handle.seek(offset)
        return handle.read()


def kill(killer, victim):
    return b"\x01" + killer + bytes(4) + victim + b"\x00"


def single_play():
    ok = True
    c, _ = player("ac")
    mark = os.path.getsize(log_path())
    c.send(bytes.fromhex("311c")); drain(c)

    c.send(bytes.fromhex("3505" "01"))
    ok &= check("single play: a win without a started stage pays nothing", not has(drain(c), b"\x09\x01"))

    c.send(bytes.fromhex("3503" "0700"))
    ok &= check("single play: stage 7 before stage 6 is refused (sSinglePlay 02)", has(drain(c), b"\x12\x02"))

    c.send(bytes.fromhex("3503" "0200"))
    ok &= check("single play: stage 2 starts (the tutorial counts as stage 1)", has(drain(c), b"\x0e\x09"))
    c.send(bytes.fromhex("3507"))
    c.send(bytes.fromhex("3505" "01"))
    ok &= check("single play: a win a moment after loading pays nothing", not has(drain(c), b"\x09\x01"))

    time.sleep(10.5)
    c.send(bytes.fromhex("3505" "01"))
    ok &= check("single play: the same win after the minimum time is paid", has(drain(c), b"\x09\x01"))

    text = log_since(mark)
    ok &= check("... and the three refusals are in the server log", text.count("Not plausible") >= 3, str(text.count("Not plausible")))
    return ok


def match():
    ok = True
    mark = os.path.getsize(log_path())
    host, host_id, others, room4 = start(1, "anticheat", guests=("ag",))
    guest, guest_id = others[0]

    for _ in range(300):
        host.send(b"\x36\x0c" + room4 + kill(host_id, guest_id))
    host.send(b"\x36\x0c" + room4 + kill(struct.pack("<I", 999999), guest_id))
    host.send(b"\x36\x0c" + room4 + kill(host_id, host_id))
    drain(host)

    finish(host, room4, [], statistics((host_id, 250, 0, 0), (guest_id, 0, 0, 0)))
    results = drain(host, 2.5)
    drain(guest, 1.0)
    host.send(b"\x36\x0c" + room4 + kill(host_id, guest_id))
    drain(host)

    text = log_since(mark)
    ok &= check("match: 300 kills in a few seconds are cut down to what the time allows",
                "Not plausible: 300 kill(s) reported" in text, text[-300:] if "300 kill" not in text else "")
    ok &= check("match: a kill by somebody who is not in the match is ignored", "kill of %d by 999999" % struct.unpack("<I", guest_id)[0] in text)
    ok &= check("match: a player killing himself is ignored", text.count("who are not both players of this match") >= 2)
    ok &= check("match: end-of-match statistics of a match this short are not used", "too short, not used" in text)
    ok &= check("match: an event after the match is over is ignored", "reported while no match is running" in text)
    ok &= check("match: the result screen still comes (sGame 03)", has(results, b"\x15\x03"), str([r.hex()[:6] for r in results]))

    payout = next((line for line in text.splitlines() if "Match payout" in line and "rh" in line), "")
    ok &= check("match: nothing is paid for it (the mode pays from 180 s on)", "+0 exp" in payout and "+0 BP" in payout, payout[-120:])
    return ok


def main():
    ok = single_play()
    ok &= match()
    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
