import glob
import os
import re
import sys

from capture_proxy import lzf_decompress, read_varlen

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NAMES = {0x41: "cChat", 0x42: "cChatRoom", 0x20: "sChat", 0x21: "sChatRoom"}


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob(os.path.join(ROOT, "logs", "capture-*.log")))[-1]
    buffers = {}
    started = {}
    for line in open(path, encoding="utf-8"):
        match = re.match(r"(\S+) \[(C\d+)\] (C->S|S->C) RAW ([0-9a-f]+)", line)
        if not match:
            continue
        time, connection, direction, payload = match.groups()
        key = (connection, direction)
        data = bytes.fromhex(payload)
        if not started.get(key):
            if (direction == "C->S" and data == b"\x2a") or (direction == "S->C" and data == b"\x04"):
                started[key] = True
            continue
        buffers[key] = buffers.get(key, b"") + data
        while True:
            header = read_varlen(buffers[key])
            if not header or len(buffers[key]) < header[0] + header[1]:
                break
            size, head = header
            body, buffers[key] = buffers[key][head:head + size], buffers[key][head + size:]
            message = lzf_decompress(body)[2:]
            if message[:1] == b"\x00":
                continue
            name = NAMES.get(message[0], "0x%02X" % message[0])
            print("%s %s %-9s %02X %s" % (time, direction, name, message[1], message[2:].hex()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
