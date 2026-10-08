import hashlib
import socket
import struct
import sys
import time

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 25100


def frame(body):
    n = len(body)
    if n <= 0x3F:
        return bytes([n]) + body
    if n <= 0x3FFF:
        return bytes([0x40 | (n >> 8), n & 0xFF]) + body
    return bytes([0x80 | (n >> 24), (n >> 16) & 0xFF, (n >> 8) & 0xFF, n & 0xFF]) + body


def lzf_literal(data):
    out = bytearray()
    for i in range(0, len(data), 32):
        chunk = data[i:i + 32]
        out.append(len(chunk) - 1)
        out += chunk
    return bytes(out)


def lzf_decompress(data):
    out, i = bytearray(), 0
    while i < len(data):
        ctrl = data[i]
        i += 1
        if ctrl < 32:
            out += data[i:i + ctrl + 1]
            i += ctrl + 1
        else:
            length = ctrl >> 5
            if length == 7:
                length += data[i]
                i += 1
            ref = len(out) - ((ctrl & 0x1F) << 8) - data[i] - 1
            i += 1
            for k in range(length + 2):
                out.append(out[ref + k])
    return bytes(out)


def rc4(key, data):
    s, j = list(range(256)), 0
    for i in range(256):
        j = (j + s[i] + key[i % len(key)]) & 255
        s[i], s[j] = s[j], s[i]
    i = j = 0
    out = bytearray()
    for b in data:
        i = (i + 1) & 255
        j = (j + s[i]) & 255
        s[i], s[j] = s[j], s[i]
        out.append(b ^ s[(s[i] + s[j]) & 255])
    return bytes(out)


def s8(text):
    raw = text.encode("latin1")
    return struct.pack("<H", len(raw)) + raw


def ws(text):
    return struct.pack("<H", len(text)) + text.encode("utf-16le")


class Client:
    def __init__(self):
        self.sock = socket.create_connection((HOST, PORT), timeout=5)
        self.buffer = b""
        self.send_seq = 0
        self.recv_seq = 0
        hello = self.sock.recv(64)
        assert len(hello) == 16 and hello[:12].rstrip(b"\0") == b"PlanB_Lobby", hello
        self.seed = struct.unpack_from("<I", hello, 12)[0]

    def login_packet(self, login_id, password):
        key = hashlib.md5(struct.pack("<I", self.seed)).digest()
        return (b"\xcc" + struct.pack("<HHI", 105, 0x6969, 0) + s8(login_id) + b"\x00\x01"
                + struct.pack("<H", len(password)) + rc4(key, password.encode()) + s8("US"))

    def login(self, login_id, password):
        self.sock.sendall(self.login_packet(login_id, password))
        return self.sock.recv(256)

    def finish_handshake(self, reply):
        assert reply[0] == 3, "expected sSessionInfo, got " + reply.hex()
        key = struct.unpack_from("<Q", reply, 1)[0]
        n = struct.unpack_from("<H", reply, 9)[0]
        write_stream = reply[11:11 + n]
        m = struct.unpack_from("<H", reply, 11 + n)[0]
        read_stream = reply[13 + n:13 + n + m]
        assert write_stream == b"TCP default write stream" and read_stream == b"TCP default read stream"
        self.sock.sendall(b"\x2a")
        start = self.sock.recv(1)
        assert start == b"\x04", start.hex()
        return key

    def send(self, message):
        plain = bytes([0x0B, self.send_seq]) + message
        self.send_seq = (self.send_seq + 1) & 255
        self.sock.sendall(frame(lzf_literal(plain)))

    def recv(self):
        while True:
            if self.buffer:
                b0 = self.buffer[0]
                kind = b0 & 0xC0
                if kind == 0:
                    size, head = b0, 1
                elif kind == 0x40 and len(self.buffer) >= 2:
                    size, head = ((b0 & 0x3F) << 8) | self.buffer[1], 2
                else:
                    size, head = None, 0
                if size is not None and len(self.buffer) >= head + size:
                    body = self.buffer[head:head + size]
                    self.buffer = self.buffer[head + size:]
                    plain = lzf_decompress(body)
                    assert plain[0] == 0x0B, plain.hex()
                    assert plain[1] == self.recv_seq, "sequence %d != %d" % (plain[1], self.recv_seq)
                    self.recv_seq = (self.recv_seq + 1) & 255
                    return plain[2:]
            data = self.sock.recv(65536)
            if not data:
                raise ConnectionError("server closed the connection")
            self.buffer += data


def check(name, condition, detail=""):
    print(("PASS " if condition else "FAIL ") + name + ("  " + detail if detail else ""))
    return condition
