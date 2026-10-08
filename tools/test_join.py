import struct
import sys
import time

from test_client import check, ws
from test_modes import MAPS, enter_channel, parse_room_list

SLOT = 26


def wstr_at(data, offset):
    length = struct.unpack_from("<H", data, offset)[0]
    return data[offset + 2:offset + 2 + length * 2].decode("utf-16le"), offset + 2 + length * 2


def parse_entered(message):
    assert message[:3] == b"\x10\x05\x00", message[:3].hex()
    room_id = struct.unpack_from("<H", message, 3)[0]
    title, p = wstr_at(message, 5)
    option, players, maximum, state, level = struct.unpack_from("<BBBBH", message, p)
    p += 6 + 6 + 2
    host, zero, bit_count = struct.unpack_from("<IBH", message, p)
    p += 7
    bits = message[p:p + (bit_count + 7) // 8]
    p += len(bits)
    slots = {}
    for index in range(bit_count):
        if bits[index // 8] >> (index % 8) & 1:
            uid, cls, _, status = struct.unpack_from("<IBBB", message, p)
            slots[index] = (uid, cls, status)
            p += SLOT
    p += 6
    host2, rule, others = struct.unpack_from("<IHB", message, p)
    p += 7
    names = []
    for _ in range(others):
        uid = struct.unpack_from("<I", message, p)[0]
        name, p = wstr_at(message, p + 4)
        p += 8 + 1 + 310
        bit_n = struct.unpack_from("<H", message, p)[0]
        p += 2
        set_bits = sum(bin(b).count("1") for b in message[p:p + (bit_n + 7) // 8])
        p += (bit_n + 7) // 8 + 9 * set_bits
        p += 3 + 4
        _, p = wstr_at(message, p)
        p += 2 + 1 + 6 + 6
        names.append((uid, name))
    return dict(id=room_id, title=title, players=players, max=maximum, state=state, level=level, host=host,
                slots=slots, rule=rule, others=names, rest=message[p:])


def drain(client, count):
    return [client.recv() for _ in range(count)]


def main():
    ok = True
    a, a_id, _ = enter_channel()
    b, b_id, _ = enter_channel()
    a_uid, b_uid = struct.unpack("<I", a_id)[0], struct.unpack("<I", b_id)[0]

    title = "Join test"
    a.send(bytes.fromhex("3200") + ws(title) + bytes([0, 0, 2, 6, 0, 0, 0, 1]))
    created = parse_entered(a.recv())
    drain(a, 2)
    ok &= check("creator: one slot, no other players", created["slots"] == {0: (a_uid, 5, 2)} and created["others"] == [], str(created["slots"]))

    b.send(bytes.fromhex("3201") + struct.pack("<H", created["id"]) + bytes(3))
    entered = parse_entered(b.recv())
    ok &= check("joiner: sRoom 05 with both slots (host ready, guest not ready)", entered["slots"] == {0: (a_uid, 5, 2), 1: (b_uid, 5, 0)}, str(entered["slots"]))
    ok &= check("joiner: 2/6 players, host is A", (entered["players"], entered["max"], entered["host"]) == (2, 6, a_uid))
    ok &= check("joiner: gets A's player record, parsed to its end",
                [uid for uid, _ in entered["others"]] == [a_uid] and set(entered["rest"]) <= {0}, str(entered["others"]))
    state, mode = b.recv(), b.recv()
    ok &= check("joiner: room state, then sMode 0C", state[:3] == b"\x10\x08\x1f" and mode == b"\x0e\x0c", mode.hex())

    joined, state = a.recv(), a.recv()
    ok &= check("host: sRoom 06 with B's id and record", joined[:2] == b"\x10\x06" and joined[2:6] == b_id, joined[:8].hex())
    p = 3 + len(ws(title))
    ok &= check("host: room state shows 2 players", state[:3] == b"\x10\x08\x1f" and state[p + 1] == 2, state[p:p + 4].hex())

    a.send(bytes.fromhex("330e"))
    for name, client in (("host", a), ("guest", b)):
        game, state, mode = client.recv(), client.recv(), client.recv()
        ok &= check("%s: sGame 00 naming A as host, state, sMode 0F" % name,
                    game[:2] == b"\x15\x00" and game[6:10] == a_id and mode == b"\x0e\x0f", game[:12].hex())

    a.send(bytes.fromhex("3709"))
    players = a.recv(); a.recv(); a.recv()
    ok &= check("host loaded: sHost 00 lists both players by slot (bitset 02 00 03)",
                players[:2] == b"\x14\x00" and players[6:10] == bytes.fromhex("01020003")
                and players[10:14] == a_id and b_id in players[14:], players[:16].hex())
    b.recv()
    b.send(bytes.fromhex("3709"))
    host_msg = a.recv()
    ok &= check("guest loaded: the host only gets the room state (it already has the list)", host_msg[:3] == b"\x10\x08\x1f", host_msg[:4].hex())
    state, mode = b.recv(), b.recv()
    ok &= check("guest loaded: guest gets state and sMode 10", mode == b"\x0e\x10", mode.hex())

    a.send(bytes.fromhex("360c0000000000") + b_id)
    a.recv()
    state, mode = b.recv(), b.recv()
    ok &= check("guest entered: guest gets sMode 11 (in match)", mode == b"\x0e\x11", mode.hex())

    b.send(bytes.fromhex("3707"))
    left, state = a.recv(), a.recv()
    ok &= check("guest left: host gets sRoom 07 with B's id", left == b"\x10\x07" + b_id, left.hex())
    ok &= check("guest left: host's room state is back to 1 player", state[p + 1] == 1, state[p:p + 4].hex())
    replies = drain(b, 4)
    ok &= check("guest left: guest is sent to the lobby (sMode 03)", b"\x0e\x03" in replies)

    a.send(bytes.fromhex("3707"))
    drain(a, 4)
    a.send(bytes.fromhex("3200") + ws("Second") + bytes([0, 0, 2, 6, 0, 0, 0, 1]))
    second = parse_entered(a.recv())
    drain(a, 2)
    b.send(bytes.fromhex("3201") + struct.pack("<H", second["id"]) + bytes(3))
    drain(b, 3)
    drain(a, 2)
    a.send(bytes.fromhex("3306"))
    replies = drain(b, 3)
    ok &= check("host left the waiting room: the guest is sent to the lobby", replies[0] == b"\x0e\x03", replies[0].hex())
    drain(a, 3)

    target = None
    deadline = time.time() + 60
    while target is None and time.time() < deadline:
        a.send(bytes.fromhex("3204") + struct.pack("<H", 1))
        rooms = None
        while rooms is None:
            message = a.recv()
            if message[:2] == b"\x0d\x00":
                rooms = parse_room_list(message)
        waiting = [room for room in rooms if room[4] == 0 and room[2] < room[3] and room[1] not in ("Second", title)]
        if waiting:
            target = waiting[0]
        else:
            time.sleep(2)

    if target is None:
        ok &= check("a waiting bot room to join (bots enabled?)", False)
    else:
        room_id, bot_title, bots_in_room = target[0], target[1], target[2]
        a.send(bytes.fromhex("3201") + struct.pack("<H", room_id) + bytes(3))
        first = a.recv()
        if first[:2] == b"\x0f\x02":
            ok &= check("bot room could be joined (it left the waiting phase just now - run again)", False, first.hex())
        else:
            taken = parse_entered(first)
            drain(a, 2)
            ok &= check("bot room '%s': the player is host in slot 0" % bot_title,
                        taken["host"] == a_uid and taken["slots"].get(0, (0,))[0] == a_uid, str(taken["slots"]))
            ok &= check("bot room: %d bot(s) seated with their records" % bots_in_room,
                        len(taken["slots"]) == bots_in_room + 1 and len(taken["others"]) == bots_in_room
                        and all(uid >= 0x7F000000 for uid, _ in taken["others"]), str(taken["others"]))
            needs = MAPS.get(taken["level"], {}).get("min", 1)
            a.send(bytes.fromhex("330e"))
            a.sock.settimeout(3)
            messages = []
            try:
                while len(messages) < 60:
                    messages.append(a.recv())
                    if messages[-1] == b"\x0e\x0f" or messages[-1][:2] == b"\x17\x04":
                        break
            except OSError:
                pass
            if needs > 1:
                ok &= check("start on a %d-player map with one real player is refused with a notice" % needs,
                            any(m[:2] == b"\x17\x04" for m in messages) and not any(m[:2] == b"\x15\x00" for m in messages))
            else:
                lefts = [m for m in messages if m[:2] == b"\x10\x07"]
                ok &= check("start: every bot leaves before the match", len(lefts) >= bots_in_room, str(len(lefts)))
                ok &= check("start: then sGame 00 and sMode 0F as for a normal room",
                            any(m[:2] == b"\x15\x00" for m in messages) and messages[-1] == b"\x0e\x0f", messages[-1].hex()[:20])

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
