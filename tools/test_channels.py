"""Channel status and lobby player count while the channels fill up.

    sServer 01   u16 count, {u8 2 (update), u16 channel, u8 status, addr}   status: 0 High, 1 Medium, 2 Low, 3 SemiMax, 4 Max
    sLobby 00    u16 players in the lobby of the channel

Sits in the lobby of channel 1 for a while (default 200 s, or SECONDS_TO_WATCH), prints
every status the server pushes on its own and the count a refresh returns, and checks that both only go up.

    python tools/test_channels.py
"""
import json
import os
import struct
import sys
import time

from test_client import check
from test_intrude import drain, player

import test_client
test_client.Client.skip_pushes = False  # this test is about those pushes

NAMES = {0: "High", 1: "Medium", 2: "Low", 3: "SemiMax", 4: "Max"}
ORDER = [2, 1, 0, 3, 4]


def states(message):
    count, = struct.unpack_from("<H", message, 2)
    at, found = 4, {}
    for _ in range(count):
        op, channel, status = struct.unpack_from("<BHB", message, at)
        found[channel] = status
        at += 4 + 6
    return found


def configured():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    with open(os.path.join(root, "config", "emulator.json"), encoding="utf-8-sig") as handle:
        return [channel["Id"] for channel in json.load(handle)["Channels"]]


def main():
    seconds = int(os.environ.get("SECONDS_TO_WATCH", "200"))
    first_id, second_id = configured()[:2]
    ok = True
    c, _ = player("ch")
    c.send(bytes.fromhex("3203") + struct.pack("<H", first_id)); drain(c)
    started = time.time()
    pushed, counts, seen, sync, listed = [], [], {}, b"", b""
    while time.time() - started < seconds:
        for reply in drain(c, 8):
            if reply[:2] == b"\x05\x01":
                pushed.append(states(reply))
                print("%4d s  pushed by the server: %s" % (time.time() - started, {k: NAMES.get(v, v) for k, v in pushed[-1].items()}))
        c.send(bytes.fromhex("3204") + struct.pack("<H", first_id))
        for reply in drain(c, 1):
            if reply[:2] == b"\x0f\x00":
                counts.append(struct.unpack_from("<H", reply, 2)[0])
            if reply[:2] == b"\x05\x01":
                seen = states(reply)
            if reply[:1] == b"\x0a":
                sync = reply
            if reply[:2] == b"\x05\x00":
                listed = reply
        print("%4d s  refresh: %d players in the lobby, channels %s" % (time.time() - started, counts[-1] if counts else -1, {k: NAMES.get(v, v) for k, v in seen.items()}))

    ok &= check("the lobby count only goes up while the channel fills", counts == sorted(counts) and counts[-1] > counts[0], str(counts[:3] + counts[-3:]))
    ok &= check("the server pushed channel states on its own (without a refresh)", len(pushed) > 0, str(len(pushed)))
    first = [state[first_id] for state in pushed if first_id in state]
    ok &= check("the first channel moves through the states in order Low, Medium, High, SemiMax, Max",
                [ORDER.index(s) for s in first] == sorted(ORDER.index(s) for s in first), str([NAMES.get(s, s) for s in first]))
    ok &= check("the first channel reaches Max and the second is still below it at that moment",
                any(state.get(first_id) == 4 and state.get(second_id) != 4 for state in pushed + [seen]), str(seen))
    ok &= check("sGlobalSync 05 with the country flag (0x20) comes with the channel list", sync[:3] == b"\x0a\x05\x20" and len(sync) == 7, sync.hex())
    length = struct.unpack_from("<H", listed, 11)[0] if listed else 0
    name = listed[13:13 + 2 * length].decode("utf-16le") if listed else ""
    fields = listed[13 + 2 * length:13 + 2 * length + 4]
    ok &= check("the first channel record: a name key of the client's string table, its type and level range, the status",
                name.startswith("_") and name.endswith("_") and len(fields) == 4 and fields[3] == seen.get(first_id), "%s %s" % (name, fields.hex()))
    if seen.get(first_id) == 4:
        other, _ = player("cx")
        other.send(bytes.fromhex("3203") + struct.pack("<H", first_id))
        replies = drain(other)
        ok &= check("entering the full channel is refused with sLobby 02 Lobby_ChannelFull (76)", replies == [bytes([0x0F, 2, 76])], str([r.hex() for r in replies]))
        other.send(bytes.fromhex("3203") + struct.pack("<H", second_id))
        ok &= check("the next channel can still be entered", any(r[:2] == b"\x05\x02" for r in drain(other)))
    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
