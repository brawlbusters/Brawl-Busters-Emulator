import os
import re
import socket
import struct
import sys
import time

from test_client import Client, check, ws

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CAPTURE = os.path.join(ROOT, "logs", "capture-20261007-211238.log")

REC_UID = bytes.fromhex("68360000")
REC_NICK = ws("sdasdas")
REC_HOST = bytes.fromhex("e59e34af330b")
REC_LOCAL = bytes.fromhex("36f5573f7f96")
REC_RELAY = bytes.fromhex("38fd5093f29d")
TITLE = "Brawl Busters Action!"


def endpoint(ip, port):
    value = struct.unpack(">I", socket.inet_aton(ip))[0]
    return struct.pack("<IH", ~value & 0xFFFFFFFF, ~port & 0xFFFF)


def recorded(sequence, name):
    pattern = re.compile(r"\S+ \[L1\] S->C seq\s+%d %s\s+([0-9a-f]+)$" % (sequence, name))
    for line in open(CAPTURE, encoding="utf-8"):
        match = pattern.match(line.rstrip("\n"))
        if match:
            return bytes.fromhex(match.group(1))
    raise KeyError((sequence, name))


CATEGORY = {"sRoom": 0x10, "sGame": 0x15, "sHost": 0x14, "sMode": 0x0E}


def main():
    if not os.path.exists(CAPTURE):
        print("capture file missing - nothing to compare against")
        return 1

    ok = True
    user = "m" + format(int(time.time() * 10) % 100000000, "x")[:8]
    nick = "N" + user[1:9]

    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    c.recv(); c.recv()
    c.send(b"\x2d\x03" + ws(nick)); c.recv()
    start = c.recv()
    uid = start[1:5]

    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(("127.0.0.1", 0))
    udp.settimeout(3)
    host = endpoint("127.0.0.1", udp.getsockname()[1])
    local = endpoint("192.168.1.50", 27000)
    udp.sendto(b"\x57" + uid + b"\x02" + local, ("127.0.0.1", 25100))
    udp.recvfrom(64)
    relay = endpoint("127.0.0.1", 25120)

    c.send(bytes.fromhex("2d0405f9010100340000000000"))
    c.recv(); c.recv()
    c.send(bytes.fromhex("2e00"))
    for _ in range(5):
        c.recv()
    c.send(bytes.fromhex("3118"))
    for _ in range(4):
        c.recv()
    c.send(bytes.fromhex("3203") + struct.pack("<H", 1))
    for _ in range(3):
        c.recv()

    def expected(sequence, name):
        data = recorded(sequence, name)
        for old, new in ((REC_UID, uid), (REC_NICK, ws(nick)), (REC_HOST, host), (REC_LOCAL, local), (REC_RELAY, relay)):
            data = data.replace(old, new)
        return bytes([CATEGORY[name]]) + data

    def compare(label, sequence, name, wildcards=()):
        nonlocal ok
        got = bytearray(c.recv())
        want = bytearray(expected(sequence, name))
        for offset, length in wildcards:
            got[offset:offset + length] = bytes(length)
            want[offset:offset + length] = bytes(length)
        detail = "" if got == want else "\n     got  %s\n     want %s" % (bytes(got).hex(), bytes(want).hex())
        ok &= check("%s = recorded %s #%d" % (label, name, sequence), got == want, detail)

    room_id = (3, 2)
    c.send(bytes.fromhex("3200") + ws(TITLE) + bytes.fromhex("0000020600000001"))
    compare("create room: entered", 59, "sRoom", [room_id])
    compare("create room: state", 60, "sRoom")
    compare("create room: sMode 0C", 61, "sMode")

    c.send(bytes.fromhex("330700a61f"))
    c.send(bytes.fromhex("33114c1f"))
    c.recv()
    compare("map + rule changed: state", 65, "sRoom")

    c.send(bytes.fromhex("330700421f"))
    compare("map changed back: state", 68, "sRoom")

    c.send(bytes.fromhex("330c"))
    compare("ready: state", 70, "sRoom")
    got = c.recv()
    ok &= check("ready: sRoom 0D 01", got == bytes.fromhex("100d01"), got.hex())

    c.send(bytes.fromhex("330e"))
    game = expected(73, "sGame")
    compare("start: sGame 00", 73, "sGame", [(2, 2), (len(game) - 10, 2), (len(game) - 6, 4)])
    compare("start: state", 74, "sRoom")
    compare("start: sMode 0F", 75, "sMode")

    c.send(bytes.fromhex("3709"))
    compare("loaded: sHost 00 (player data)", 76, "sHost", [(2, 2)])
    compare("loaded: state", 77, "sRoom")
    compare("loaded: sMode 10", 78, "sMode")

    c.send(bytes.fromhex("36090400000000" + "00"))
    c.send(bytes.fromhex("360c0400000000") + uid)
    compare("match running: state", 79, "sRoom")
    compare("match running: sMode 11", 80, "sMode")

    c.send(bytes.fromhex("361304000000"))
    got = c.recv()
    ok &= check("match ended: empty record update", got == bytes.fromhex("0901") + bytes(10), got.hex())

    c.send(bytes.fromhex("360a04000000"))
    got = c.recv()
    state_at = 3 + len(ws(TITLE)) + 3
    ok &= check("match closed: room state back to waiting", got[:3] == bytes.fromhex("10081f") and got[state_at] == 0, got[:8].hex())
    got = c.recv()
    ok &= check("match closed: sMode 0C (waiting room)", got == bytes.fromhex("0e0c"), got.hex())

    c.send(bytes.fromhex("3306"))
    got = c.recv()
    ok &= check("leave: sMode 03", got == bytes.fromhex("0e03"), got.hex())
    got = c.recv()
    ok &= check("leave: sLobby 04", got == bytes.fromhex("0f04"), got.hex())

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
