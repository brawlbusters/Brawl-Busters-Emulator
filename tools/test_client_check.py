"""The check of the client's game tables (Security/ClientCheck.cs), spoken the way bin/LightFX.dll speaks it.

    "BBIC" 01              -> "BBIC" 02 + 16 bytes challenge
    "BBIC" 03 + 32 bytes   -> "BBIC" 04 + 1 (verified) / 0          proof = SHA-256(challenge + digest + salt)

    python tools/test_client_check.py            (server with AntiCheat.ClientCheck "log", the default, or "require")
"""
import hashlib
import os
import socket
import struct
import sys

from bus_tool import ENTRY_SIZE, HEADER_OFFSET, HEADER_SIZE, NAME_SIZE, xor
from test_client import check

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "..", "Data", "xmandb.bus")
PORT = int(os.environ.get("BB_LOBBY_PORT", "27100"))
SALT = b"BrawlBusters client check v1"


def digest(data):
    """Name, size and stored bytes of every file of the archive, without the server address table."""
    header = xor(data[HEADER_OFFSET:HEADER_OFFSET + HEADER_SIZE])
    count = struct.unpack_from("<I", header, NAME_SIZE + 4)[0]
    table = HEADER_OFFSET + HEADER_SIZE
    payloads = table + count * ENTRY_SIZE
    sha = hashlib.sha256()
    for index in range(count):
        entry = xor(data[table + index * ENTRY_SIZE:table + (index + 1) * ENTRY_SIZE])
        name = entry[:NAME_SIZE].split(b"\0")[0]
        offset, size = struct.unpack_from("<II", entry, NAME_SIZE)
        if b"clientconfigdb" in name.lower():
            continue
        sha.update(name + struct.pack("<I", size) + data[payloads + offset:payloads + offset + size])
    return sha.digest()


def prove(tables):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.settimeout(3)
        s.sendto(b"BBIC\x01", ("127.0.0.1", PORT))
        challenge = s.recv(64)
        if challenge[:5] != b"BBIC\x02" or len(challenge) != 21:
            return None
        s.sendto(b"BBIC\x03" + hashlib.sha256(challenge[5:] + tables + SALT).digest(), ("127.0.0.1", PORT))
        verdict = s.recv(64)
        return verdict[5] if verdict[:5] == b"BBIC\x04" else None


def main():
    ok = True
    data = open(ARCHIVE, "rb").read()
    tables = digest(data)

    ok &= check("the tables of the unchanged archive are verified", prove(tables) == 1)

    changed = bytearray(data)
    changed[len(changed) // 2] ^= 1          # one bit of one table
    ok &= check("one changed bit in a table: not verified", prove(digest(bytes(changed))) == 0)

    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.settimeout(3)
        s.sendto(b"BBIC\x01", ("127.0.0.1", PORT))
        first = s.recv(64)[5:]
        answer = b"BBIC\x03" + hashlib.sha256(first + tables + SALT).digest()
        s.sendto(answer, ("127.0.0.1", PORT))
        accepted = s.recv(64)[5]
        s.sendto(b"BBIC\x01", ("127.0.0.1", PORT))
        s.recv(64)
        s.sendto(answer, ("127.0.0.1", PORT))
        replayed = s.recv(64)[5]
    ok &= check("a recorded answer is worthless for the next challenge", (accepted, replayed) == (1, 0), str((accepted, replayed)))

    ok &= check("... and the address is verified again by a fresh proof", prove(tables) == 1)
    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
