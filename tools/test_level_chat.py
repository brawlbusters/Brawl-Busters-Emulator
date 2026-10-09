import json
import os
import struct
import sys

from test_chat_room import ChatClient
from test_client import check, ws
from test_shop import new_player_at_home

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def level_bytes(record, nickname):
    offset = 2 + len(ws(nickname)) + 8 + 1 + 5 * 62
    bits = struct.unpack_from("<H", record, offset)[0]
    set_bits = sum(bin(b).count("1") for b in record[offset + 2:offset + 2 + (bits + 7) // 8])
    offset += 2 + (bits + 7) // 8 + 9 * set_bits
    return record[offset], record[offset + 1], record[offset + 2]


def main():
    ok = True

    from test_client import Client
    import time
    user = "v" + format(int(time.time() * 10) % 100000000, "x")[:8]
    nick = "N" + user[1:9]
    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    c.recv(); c.recv()
    c.send(b"\x2d\x03" + ws(nick)); c.recv(); c.recv()
    c.send(bytes.fromhex("2d0403f6010400160000000000"))
    record = c.recv()
    ok &= check("new player record: level sent as 1, gem rank 0, third byte 8", level_bytes(record, nick) == (1, 0, 8),
                str(level_bytes(record, nick)))
    c.sock.close()

    accounts = json.load(open(os.path.join(ROOT, "data", "accounts.json"), encoding="utf-8"))
    raised = [a for a in accounts if a.get("level", 0) >= 30 and a.get("Gold") == 999999999 and a.get("Cash") == 999999999]
    ok &= check("accounts raised to level 30 with full BP and RT", len(raised) >= 1, "%d account(s)" % len(raised))

    chat = ChatClient(1, "alice")
    chat.send(bytes.fromhex("4127"))
    ok &= check("0x27 buddy list request -> sChat 11 with 0 buddies", chat.recv() == bytes.fromhex("20110000"))
    chat.send(bytes.fromhex("413501000000000000000400"))
    chat.send(bytes.fromhex("411a"))
    chat.send(bytes.fromhex("412b"))
    chat.send(bytes.fromhex("4128") + ws("test"))
    ok &= check("0x28 add buddy -> sChat 15 02 (recorded 'not found'); presence notices unanswered",
                chat.recv() == bytes.fromhex("201502"))
    chat.send(bytes.fromhex("4127"))
    ok &= check("second buddy list request answered", chat.recv() == bytes.fromhex("20110000"))

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
