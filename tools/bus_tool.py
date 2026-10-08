"""List or extract Brawl Busters .bus archives (Data/*.bus).

    python tools/bus_tool.py list    ../Data/xmandb.bus
    python tools/bus_tool.py extract ../Data/xmandb.bus out_dir

Format (pbclient.exe loader 0x52B8BA, cipher 0x50B91E, key setup 0x50AEA3):

    0x000  33 bytes   unused (zero)
    0x021  272 bytes  header, XOR-obfuscated
                        char path[264]
                        u32  unused
                        u32  file count
    0x131  count x 276-byte entries, each XOR-obfuscated on its own
                        char name[264]
                        u32  offset (relative to the end of the entry table)
                        u32  size
                        u8   unused
                        u8   encrypted flag (payload XOR-obfuscated when 1)
                        u8   padding[2]
    ...    file payloads

The XOR key is 4096 bytes and restarts at every header, entry and payload:
    key[i] = (mt19937(seed 0x6D2C1B76).next() // 16384) % 105
"""
import os
import struct
import sys

HEADER_OFFSET = 0x21
HEADER_SIZE = 0x110
ENTRY_SIZE = 0x114
NAME_SIZE = 0x108
KEY_SIZE = 0x1000


def _mt19937(seed):
    n = 624
    state = [0] * n
    state[0] = seed
    for i in range(1, n):
        state[i] = (1812433253 * (state[i - 1] ^ (state[i - 1] >> 30)) + i) & 0xFFFFFFFF
    index = n
    while True:
        if index >= n:
            for k in range(n):
                y = (state[k] & 0x80000000) | (state[(k + 1) % n] & 0x7FFFFFFF)
                state[k] = state[(k + 397) % n] ^ (y >> 1) ^ (0x9908B0DF if y & 1 else 0)
            index = 0
        y = state[index]
        index += 1
        y ^= y >> 11
        y ^= (y << 7) & 0x9D2C5680
        y ^= (y << 15) & 0xEFC60000
        y ^= y >> 18
        yield y


def _make_key():
    generator = _mt19937(0x6D2C1B76)
    return bytes((next(generator) // 16384) % 105 for _ in range(KEY_SIZE))


KEY = _make_key()


def xor(data):
    return bytes(b ^ KEY[i % KEY_SIZE] for i, b in enumerate(data))


def _text(raw):
    return raw.split(b"\0")[0].decode("cp949", "replace")


def read_archive(path):
    with open(path, "rb") as handle:
        data = handle.read()

    header = xor(data[HEADER_OFFSET:HEADER_OFFSET + HEADER_SIZE])
    count = struct.unpack_from("<I", header, NAME_SIZE + 4)[0]
    table = HEADER_OFFSET + HEADER_SIZE
    payload_base = table + count * ENTRY_SIZE

    entries = []
    for i in range(count):
        entry = xor(data[table + i * ENTRY_SIZE:table + (i + 1) * ENTRY_SIZE])
        offset, size, _, encrypted = struct.unpack_from("<IIBB", entry, NAME_SIZE)
        entries.append((_text(entry[:NAME_SIZE]), payload_base + offset, size, bool(encrypted)))
    return data, _text(header[:NAME_SIZE]), entries


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ("list", "extract"):
        print(__doc__)
        return 1

    data, root, entries = read_archive(sys.argv[2])
    print("%s  (%d files)" % (root, len(entries)))

    if sys.argv[1] == "list":
        for name, _, size, encrypted in entries:
            print("%10d  %s  %s" % (size, "enc" if encrypted else "   ", name))
        return 0

    out_dir = sys.argv[3] if len(sys.argv) > 3 else "extracted"
    for name, offset, size, encrypted in entries:
        payload = data[offset:offset + size]
        if encrypted:
            payload = xor(payload)
        target = os.path.join(out_dir, name)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "wb") as handle:
            handle.write(payload)
        print("  " + name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
