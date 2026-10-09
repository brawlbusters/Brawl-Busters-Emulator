import struct
import sys
import time

from test_client import first_channel, Client, check, ws


def expect(client, name, expected_hex):
    message = client.recv()
    return check(name, message == bytes.fromhex(expected_hex), message.hex()[:90])


def new_player_at_home():
    user = "s" + format(int(time.time() * 10) % 100000000, "x")[:8]
    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    c.recv()
    c.recv()
    c.send(b"\x2d\x03" + ws("N" + user[1:9]))
    c.recv()
    c.recv()
    c.send(bytes.fromhex("2d0403f6010400160000000000"))
    c.recv()
    c.recv()
    c.send(bytes.fromhex("2e00"))
    for _ in range(5):
        c.recv()
    return c


def main():
    ok = True
    c = new_player_at_home()

    c.send(bytes.fromhex("311b"))
    ok &= expect(c, "open shop -> sMode 07", "0e07")
    ok &= expect(c, "open shop -> empty inventory list", "0b00" + "0100" + "00" * 20)

    c.send(bytes.fromhex("301e97130000000000000000000101"))
    ok &= expect(c, "buy package 5015 -> sStore 1C 00", "0c1c00")
    ok &= expect(c, "buy package 5015 -> gold 16200", "090100000000000000000200483f0000")
    ok &= expect(c, "buy package 5015 -> item added in slot 1",
                 "0b010101010001001897130000000000000000010001ffffffff")

    c.send(bytes.fromhex("301efb000000000000000000000101"))
    ok &= expect(c, "buy item 251 -> sStore 1C 00", "0c1c00")
    ok &= expect(c, "buy item 251 -> gold 15200", "090100000000000000000200603b0000")
    message = c.recv()
    ok &= check("buy item 251 -> item added with type 0E",
                message == bytes.fromhex("0b0101010200" "0200" "0efb000000000000000000010001ffffffff"), message.hex())

    c.send(bytes.fromhex("301e97130000000000000000000101"))
    ok &= expect(c, "third purchase refused: sStore 1D Store_NoGold (55, \"You have insufficient BP\")", "0c1d37")

    c.send(bytes.fromhex("311a"))
    ok &= expect(c, "open inventory -> sMode 06", "0e06")
    ok &= expect(c, "open inventory -> empty record update", "0901" + "00" * 10)

    c.send(bytes.fromhex("311d"))
    ok &= expect(c, "open records -> sMode 0A", "0e0a")
    c.send(bytes.fromhex("3e01"))
    message = c.recv()
    ok &= check("my records -> 670 zero bytes", message == bytes([0x13, 9]) + bytes(670), str(len(message)))
    ok &= expect(c, "my records -> end marker", "130c01")

    c.send(bytes.fromhex("311f"))
    ok &= expect(c, "open capsule machine -> sMode 14", "0e14")
    message = c.recv()
    ok &= check("capsule machine -> inventory list with both items",
                message[:4] == bytes.fromhex("0b000300") and len(message) == 2 + 22 + 2 * 20, message.hex()[:60])

    c.send(bytes.fromhex("3117"))
    ok &= expect(c, "back home -> sMode 02", "0e02")
    c.recv()

    host = new_player_at_home()
    for client in (c, host):
        client.send(bytes.fromhex("3118"))
        for _ in range(2):
            client.recv()
        client.send(bytes.fromhex("3203") + struct.pack("<H", first_channel()))
        for _ in range(3):
            client.recv()

    title = "Brawl Busters Action!"
    host.send(bytes.fromhex("3200") + ws(title) + bytes.fromhex("0000020600000001"))
    for _ in range(3):
        host.recv()

    c.send(bytes.fromhex("3204") + struct.pack("<H", 1))
    for _ in range(4):
        c.recv()
    message = c.recv()
    ok &= check("another player sees the room in the list",
                message[:2] == bytes.fromhex("0d00") and ws(title) in message, message.hex()[:80])
    tail = message[message.index(ws(title)) + len(ws(title)):]
    ok &= check("room entry as recorded (08, players, max, state, level; state 0 = waiting)",
                tail[:6] == bytes.fromhex("08010600411f") and tail[12:14] == bytes.fromhex("0200"), tail[:14].hex())

    host.send(bytes.fromhex("3306"))
    for _ in range(3):
        host.recv()
    c.send(bytes.fromhex("3204") + struct.pack("<H", 1))
    for _ in range(4):
        c.recv()
    message = c.recv()
    ok &= check("room disappears when its host leaves",
                message[:2] == bytes.fromhex("0d00") and ws(title) not in message, message.hex()[:80])

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
