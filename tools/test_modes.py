import json
import os
import socket
import struct
import sys
import time

from test_client import Client, check, ws

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = json.load(open(os.path.join(ROOT, "data", "game", "maps.json"), encoding="utf-8"))
MAPS = {entry["id"]: entry for entry in DATA["maps"]}
RULES = {entry["id"]: entry for entry in DATA["rules"]}
RELAY = struct.pack("<IH", ~0x7F000001 & 0xFFFFFFFF, ~25120 & 0xFFFF)

MODES = {1: ("team deathmatch", 16), 2: ("survival", 6), 4: ("jessium", 16), 6: ("SZM", 16),
         7: ("ZIM", 16), 8: ("free for all", 16), 9: ("BSR", 4), 10: ("channel 5 team", 8)}

counter = 0


def enter_channel():
    global counter
    counter += 1
    user = "g" + format(int(time.time() * 10) % 10000000, "x") + format(counter, "x")
    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    c.recv(); c.recv()
    c.send(b"\x2d\x03" + ws("N" + user[1:])); c.recv()
    uid = c.recv()[1:5]
    c.send(bytes.fromhex("2d0405f9010100340000000000"))
    c.recv(); c.recv()
    c.send(bytes.fromhex("2e00"))
    for _ in range(5):
        c.recv()
    c.send(bytes.fromhex("3118"))
    for _ in range(4):
        c.recv()
    c.send(bytes.fromhex("3203") + struct.pack("<H", 1))
    replies = [c.recv() for _ in range(3)]
    return c, uid, replies


def create(c, title, mode, max_players):
    c.send(bytes.fromhex("3200") + ws(title) + bytes([0, 0, mode, max_players, 0, 0, 0, 1]))
    entered, state, screen = c.recv(), c.recv(), c.recv()
    return entered, state, screen


def parse_state(state, title, uid):
    p = 3 + len(ws(title))
    option, players, max_players, phase, seven, level = struct.unpack_from("<BBBBBH", state, p)
    k = state.index(b"\x07" + RELAY + uid) + 11
    return max_players, phase, level, struct.unpack_from("<H", state, k)[0]


def default_rule(level):
    rules = MAPS[level]["rules"]
    return next((rule for rule in rules if RULES[rule]["default"]), rules[0])


def parse_room_list(message):
    count = struct.unpack_from("<H", message, 2)[0]
    p, rooms = 4, []
    for _ in range(count):
        room_id, length = struct.unpack_from("<HH", message, p)
        p += 4
        title = message[p:p + length * 2].decode("utf-16le")
        p += length * 2
        option, players, max_players, state, level = struct.unpack_from("<BBBBH", message, p)
        p += 6 + 6 + 2
        rooms.append((room_id, title, players, max_players, state, level))
    assert p == len(message), "room list has trailing bytes"
    return rooms


def main():
    ok = True
    hosts = []

    for mode, (name, asked) in sorted(MODES.items()):
        c, uid, _ = enter_channel()
        hosts.append(c)
        title = "Mode %d" % mode
        entered, state, screen = create(c, title, mode, asked)
        max_players, phase, level, rule = parse_state(state, title, uid)
        info = MAPS.get(level, {})
        good = (entered[:2] == b"\x10\x05" and screen == b"\x0e\x0c" and info.get("mode") == mode
                and info.get("default") and max_players == min(asked, info["max"]) and rule == default_rule(level))
        ok &= check("mode %2d %-16s -> map %5d %-30s rule %5d, max %2d" % (mode, name, level, info.get("name"), rule, max_players), good)

        if mode == 4:
            others = [m for m in DATA["maps"] if m["mode"] == mode and m["released"] and m["id"] != level]
            picked = others[-1]
            c.send(b"\x33\x07\x00" + struct.pack("<H", picked["id"]))
            got = parse_state(c.recv(), title, uid)
            ok &= check("  map change to %d %s accepted, rule %d" % (picked["id"], picked["name"], got[3]),
                        got[2] == picked["id"] and got[3] in picked["rules"])
            c.send(b"\x33\x07\x00" + struct.pack("<H", 8001))
            got = parse_state(c.recv(), title, uid)
            ok &= check("  survival map 8001 refused in a jessium room", got[2] == picked["id"])
            wanted = [r for r in picked["rules"] if r != got[3]][0]
            c.send(b"\x33\x11" + struct.pack("<H", wanted))
            got = parse_state(c.recv(), title, uid)
            ok &= check("  rule change to %d accepted" % wanted, got[3] == wanted)
            c.send(b"\x33\x11" + struct.pack("<H", 8002))
            got = parse_state(c.recv(), title, uid)
            ok &= check("  survival rule 8002 refused -> %d" % got[3], got[3] in picked["rules"])

    c, uid, _ = enter_channel()
    hosts.append(c)
    entered, state, screen = create(c, "All maps", 2, 6)
    survival = [m for m in DATA["maps"] if m["mode"] == 2 and m["released"]]
    accepted = 0
    for entry in survival:
        c.send(b"\x33\x07\x00" + struct.pack("<H", entry["id"]))
        got = parse_state(c.recv(), "All maps", uid)
        accepted += got[2] == entry["id"] and got[3] in entry["rules"]
    ok &= check("all %d released survival maps selectable with a valid rule" % len(survival), accepted == len(survival))

    viewer, _, replies = enter_channel()
    listing = next((m for m in replies if m[:2] == b"\x0d\x00"), None)
    ok &= check("room list received", listing is not None)
    if listing is not None:
        rooms = {title: rest for _, title, *rest in parse_room_list(listing)}
        mine = [title for title in rooms if title.startswith("Mode ") or title == "All maps"]
        ok &= check("room list has all %d rooms of this test" % len(hosts), len(mine) == len(hosts), str(sorted(mine)))
        bsr = rooms.get("Mode 9")
        ok &= check("room list entry: BSR room 1/4 players, waiting, default BSR map",
                    bsr is not None and bsr[0] == 1 and bsr[1] == 4 and bsr[2] == 0 and MAPS[bsr[3]]["mode"] == 9, str(bsr))

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
