import socket
import struct
import sys
import time

from test_client import Client, check, frame, lzf_decompress, lzf_literal, ws

CHAT_PORT = 25900


class ChatClient:
    def __init__(self, user_id, nickname):
        self.sock = socket.create_connection(("127.0.0.1", CHAT_PORT), timeout=5)
        self.buffer = b""
        self.send_seq = 0
        greeting = self.sock.recv(64)
        assert greeting == b"PlanB_Chat\0", greeting
        self.sock.sendall(b"\x28" + struct.pack("<HI", 0x9A, user_id) + ws(nickname))
        assert self.sock.recv(256)[0] == 3
        self.sock.sendall(b"\x2a")
        assert self.sock.recv(1) == b"\x04"

    def send(self, message):
        self.sock.sendall(frame(lzf_literal(bytes([0x0B, self.send_seq]) + message)))
        self.send_seq = (self.send_seq + 1) & 255

    def recv(self):
        while True:
            if self.buffer:
                first = self.buffer[0]
                if first & 0xC0 == 0:
                    size, head = first, 1
                elif len(self.buffer) >= 2:
                    size, head = ((first & 0x3F) << 8) | self.buffer[1], 2
                else:
                    size, head = None, 0
                if size is not None and len(self.buffer) >= head + size:
                    body, self.buffer = self.buffer[head:head + size], self.buffer[head + size:]
                    return lzf_decompress(body)[2:]
            self.buffer += self.sock.recv(65536)


def main():
    ok = True

    a = ChatClient(1, "alice")
    b = ChatClient(2, "bob")
    a.send(bytes.fromhex("4127"))
    a.send(bytes.fromhex("413501000000000000000400"))
    ok &= check("chat login reply (sChat 11 0000)", a.recv() == bytes.fromhex("20110000"))
    a.send(bytes.fromhex("4200") + struct.pack("<H", 1))
    ok &= check("join chat room (sChatRoom 04 id 0100)", a.recv() == bytes.fromhex("210401000100"))
    b.send(bytes.fromhex("4200") + struct.pack("<H", 1))
    b.recv()
    a.send(bytes.fromhex("4124") + struct.pack("<H", 1) + ws("hi"))
    expected = bytes.fromhex("200d0000") + ws("alice") + ws("hi")
    ok &= check("sender sees its message", a.recv() == expected)
    ok &= check("other member sees the message", b.recv() == expected)
    a.send(bytes.fromhex("4201") + struct.pack("<H", 1))
    ok &= check("leave chat room (sChatRoom 05)", a.recv() == bytes.fromhex("2105"))

    user = "r" + format(int(time.time()) % 100000000, "x")[:8]
    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    c.recv()
    c.recv()
    title = "Brawl Busters Action!"
    c.send(bytes.fromhex("3200") + ws(title) + bytes.fromhex("0000020600000000"))
    entered = c.recv()
    state = c.recv()
    ok &= check("sRoom 05 with the title", entered[:3] == bytes([0x10, 5, 0]) and ws(title) in entered)
    ok &= check("sRoom 05 has the recorded length", len(entered) == 129, str(len(entered)))
    ok &= check("sRoom 08 with the title", state[:3] == bytes([0x10, 8, 0x1F]) and ws(title) in state)
    ok &= check("sRoom 08 has the recorded length", len(state) == 129, str(len(state)))
    ok &= check("recorded player id was replaced", bytes.fromhex("67360000") not in entered + state)
    ok &= check("sMode 0C (waiting room)", c.recv() == bytes.fromhex("0e0c"))
    c.send(bytes.fromhex("3306"))
    ok &= check("leave room -> sMode 03", c.recv() == bytes.fromhex("0e03"))
    ok &= check("leave room -> sLobby 04", c.recv() == bytes.fromhex("0f04"))

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
