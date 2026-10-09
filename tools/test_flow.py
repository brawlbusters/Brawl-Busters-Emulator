import struct
import sys
import time

from test_client import first_channel, Client, check, s8, ws

CREATE_CHARACTER = bytes.fromhex("2d0403f6010400160000000000")
TUTORIAL_DONE = bytes.fromhex("2e00")
ENTER_SINGLE_LOBBY = bytes.fromhex("311c")
ENTER_LOBBY = bytes.fromhex("3118")
ENTER_CHANNEL = bytes.fromhex("3203") + struct.pack("<H", first_channel())
REFRESH_LOBBY = bytes.fromhex("3204") + struct.pack("<H", 1)
UDP_OK = bytes.fromhex("3a00010200")
KEEP_ALIVE = bytes.fromhex("0000")
USER_INFO_TICK = bytes.fromhex("2c046e00")

RECORD_AFTER_NICK = bytes.fromhex(
    "0000000000000000" "03" "f6010400160000000000" "61ea00005d2bed2c1127")


def expect(client, name, expected):
    message = client.recv()
    return check(name, message == expected, message.hex()[:80])


def main():
    ok = True
    user = "f" + format(int(time.time()) % 100000000, "x")[:8]
    nick = "K" + user[1:9]

    c = Client()
    reply = c.login(user, "pass1234")
    ok &= check("login accepted", reply[0] == 3)
    c.finish_handshake(reply)
    ok &= expect(c, "sMode 0 (intro) after session start", bytes.fromhex("0e00"))
    ok &= expect(c, "keep-alive after session start", bytes.fromhex("0000"))

    c.send(b"\x2d\x02" + ws(nick))
    ok &= expect(c, "nickname check -> sIntro 0 success", bytes.fromhex("080001"))
    c.send(b"\x2d\x03" + ws(nick))
    ok &= expect(c, "nickname create -> sIntro 1 success", bytes.fromhex("080101"))
    message = c.recv()
    ok &= check("sUserStart follows nickname creation",
                message[0] == 6 and message[5:] .startswith(s8(user) + ws(nick) + bytes(8)), message.hex()[:60])

    c.send(UDP_OK)
    c.send(KEEP_ALIVE)
    c.send(CREATE_CHARACTER)
    message = c.recv()
    head = bytes([9, 0]) + ws(nick)
    ok &= check("full player record: header and nickname", message.startswith(head))
    ok &= check("full player record: class, shape and first equipment as recorded",
                message[len(head):len(head) + len(RECORD_AFTER_NICK)] == RECORD_AFTER_NICK)
    ok &= check("full player record: length matches the recording",
                len(message) == 657 + 2 * (len(nick) - 6) - 12, str(len(message)))
    ok &= expect(c, "sMode 1 (tutorial)", bytes.fromhex("0e01"))

    c.send(TUTORIAL_DONE)
    ok &= expect(c, "sMode 2 (home)", bytes.fromhex("0e02"))
    message = c.recv()
    ok &= check("partial record 1 as recorded", message[:2] == bytes([9, 1]) and len(message) == 140, str(len(message)))
    message = c.recv()
    ok &= check("partial record 2 as recorded", message[:2] == bytes([9, 1]) and len(message) == 153, str(len(message)))
    message = c.recv()
    ok &= check("channel list", message[:2] == bytes([5, 0]) and message[2] >= 1)
    message = c.recv()
    ok &= check("channel states", message[:2] == bytes([5, 1]))

    c.send(USER_INFO_TICK)
    c.send(ENTER_SINGLE_LOBBY)
    for name, expected in (("sMode 8 (single lobby)", "0e08"), ("single-play state", "1200010000")):
        ok &= expect(c, name, bytes.fromhex(expected))

    c.send(ENTER_LOBBY)
    for name, expected in (("sMode 3 (lobby)", "0e03"), ("sLobby 4 (opened)", "0f04")):
        ok &= expect(c, name, bytes.fromhex(expected))

    c.send(ENTER_CHANNEL)
    ok &= expect(c, "channel changed", bytes.fromhex("05020100"))
    message = c.recv()
    ok &= check("player count", message[:2] == bytes.fromhex("0f00") and len(message) == 4, message.hex())
    message = c.recv()
    ok &= check("room list", message[:2] == bytes.fromhex("0d00"), message.hex()[:40])

    c.send(REFRESH_LOBBY)
    message = c.recv()
    ok &= check("refresh: channel list", message[:2] == bytes([5, 0]))
    c.recv()
    ok &= expect(c, "refresh: channel changed", bytes.fromhex("05020100"))
    c.sock.close()

    c = Client()
    reply = c.login(user, "wrongpass")
    ok &= check("wrong password -> sLoginFailed / PW_WrongPassword", reply == bytes([1, 19]), reply.hex())
    reply = c.login(user, "pass1234")
    ok &= check("retry on the same connection accepted", reply[0] == 3)
    c.finish_handshake(reply)
    ok &= expect(c, "returning: sMode 0", bytes.fromhex("0e00"))
    c.recv()
    message = c.recv()
    ok &= check("returning: sUserStart with nickname", message[0] == 6 and ws(nick) in message)
    message = c.recv()
    ok &= check("returning: full player record", message[:2] == bytes([9, 0]))
    ok &= expect(c, "returning: sMode 2 (home)", bytes.fromhex("0e02"))
    c.sock.close()

    c = Client()
    reply = c.login("ab", "pass1234")
    ok &= check("short id -> sCreateIDFailed / ID_TooShort", reply == bytes([2, 7]), reply.hex())
    c.sock.close()

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
