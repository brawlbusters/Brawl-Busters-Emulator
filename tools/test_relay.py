"""UDP relay: packets between two players who cannot reach each other directly.

    register   58 <user id> 0B           ->  58 <user id> 0E 01
    data       58 <A> 0D <B> payload     ->  the same datagram, delivered to whichever of A and B did not send it

The client builds that 10-byte header itself (PbUDPManager, connection in relay mode) and both sides
use the same one, so the relay tells the direction from the sender's address.

    python tools/test_relay.py
"""
import json
import os
import socket
import struct
import sys
import time

from test_client import check

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORT = int(os.environ["BB_RELAY_PORT"]) if "BB_RELAY_PORT" in os.environ else json.load(open(os.path.join(ROOT, "config", "emulator.json"), encoding="utf-8-sig")).get("RelayPort", 25120)
RELAY = ("127.0.0.1", PORT)


def receive(sock, wait=1.0):
    sock.settimeout(wait)
    try:
        return sock.recvfrom(2048)[0]
    except (socket.timeout, OSError):
        return b""


def main():
    ok = True
    a_id, b_id, c_id = (int(time.time()) % 100000) + 910000, (int(time.time()) % 100000) + 920000, 930000
    a, b, c = (socket.socket(socket.AF_INET, socket.SOCK_DGRAM) for _ in range(3))

    for sock, uid in ((a, a_id), (b, b_id)):
        sock.sendto(b"\x58" + struct.pack("<I", uid) + b"\x0b", RELAY)
        ok &= check("player %d registers with the relay: 58 id 0E 01" % uid, receive(sock) == b"\x58" + struct.pack("<I", uid) + b"\x0e\x01")

    header = b"\x58" + struct.pack("<I", a_id) + b"\x0d" + struct.pack("<I", b_id)
    a.sendto(header + b"hello host", RELAY)
    ok &= check("A -> relay: B receives the datagram unchanged", receive(b) == header + b"hello host")
    b.sendto(header + b"hello guest", RELAY)
    ok &= check("B -> relay with the same header: A receives it", receive(a) == header + b"hello guest")
    ok &= check("nothing is echoed back to the sender", receive(b, 0.3) == b"")

    c.sendto(header + b"intruder", RELAY)
    ok &= check("a third address using that header reaches nobody", receive(a, 0.3) == b"" and receive(b, 0.3) == b"")

    other = b"\x58" + struct.pack("<I", a_id) + b"\x0d" + struct.pack("<I", c_id)
    a.sendto(other + b"x", RELAY)
    ok &= check("data for a player who never registered is dropped", receive(c, 0.3) == b"")

    a.sendto(b"\x58" + struct.pack("<I", a_id) + b"\x0c", RELAY)
    ok &= check("keep-alive gets no reply", receive(a, 0.3) == b"")

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
