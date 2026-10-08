import json
import os
import struct
import sys
import time

from test_client import check
from test_modes import MAPS, enter_channel, parse_room_list

STATE = {0: "waiting", 5: "ready", 3: "starting", 1: "loading", 2: "playing"}
WATCH_SECONDS = 75


def refresh(client, channel):
    client.send(bytes.fromhex("3204") + struct.pack("<H", channel))
    players, rooms = None, None
    while rooms is None:
        message = client.recv()
        if message[:2] == b"\x0f\x00":
            players = struct.unpack_from("<H", message, 2)[0]
        elif message[:2] == b"\x0d\x00":
            rooms = parse_room_list(message)
    return players, rooms


def main():
    ok = True
    client, _, _ = enter_channel()

    seen_rooms, seen_modes, seen_maps, seen_states = {}, set(), set(), set()
    most_bots, problems = 0, []
    deadline = time.time() + WATCH_SECONDS
    while time.time() < deadline:
        total = 0
        for channel in (1, 2):
            players, rooms = refresh(client, channel)
            total += (players or 1) - 1
            for room_id, title, count, maximum, state, level in rooms:
                if title.startswith("Mode ") or title == "All maps":
                    continue
                info = MAPS.get(level)
                if info is None or not info["released"]:
                    problems.append("room %d: map %d is not a released map" % (room_id, level))
                    continue
                if not 1 <= count <= maximum <= info["max"]:
                    problems.append("room %d: %d/%d players on a %d player map" % (room_id, count, maximum, info["max"]))
                if state not in STATE:
                    problems.append("room %d: unknown state %d" % (room_id, state))
                if state == 2 and count < info["min"]:
                    problems.append("room %d: playing with %d players, map needs %d" % (room_id, count, info["min"]))
                seen_rooms[(channel, room_id)] = (title, info["mode"], info["name"])
                seen_modes.add(info["mode"])
                seen_maps.add(level)
                seen_states.add(state)
                total += count
        most_bots = max(most_bots, total)
        time.sleep(3)

    _, rooms = refresh(client, 1)
    if rooms:
        client.send(bytes.fromhex("3201") + struct.pack("<H", rooms[0][0]) + bytes(3))
        got = client.recv()
        ok &= check("join a bot room: sLobby 02 + Lobby_CannotJoinRoom (73)", got == bytes.fromhex("0f0249"), got.hex())
    client.send(bytes.fromhex("3201") + struct.pack("<H", 60000) + bytes(3))
    got = client.recv()
    ok &= check("join a missing room: sLobby 02 + Lobby_NotExistRoom (74)", got == bytes.fromhex("0f024a"), got.hex())

    for (channel, room_id), (title, mode, name) in sorted(seen_rooms.items()):
        print("   channel %d room %3d  mode %2d  %-32s '%s'" % (channel, room_id, mode, name, title))

    ok &= check("no invalid room seen", not problems, "; ".join(problems[:5]))
    ok &= check("bots opened several rooms (%d)" % len(seen_rooms), len(seen_rooms) >= 5)
    ok &= check("rooms in different game modes (%s)" % sorted(seen_modes), len(seen_modes) >= 3)
    ok &= check("rooms on different maps (%d)" % len(seen_maps), len(seen_maps) >= 4)
    ok &= check("rooms seen waiting and playing (%s)" % sorted(STATE[s] for s in seen_states), {0, 2} <= seen_states)
    ok &= check("30 bots accounted for at some point (%d in lobbies and rooms)" % most_bots, 25 <= most_bots <= 30)

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
