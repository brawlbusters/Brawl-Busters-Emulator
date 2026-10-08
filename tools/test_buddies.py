import struct
import sys
import time

from test_chat_room import ChatClient
from test_client import Client, check, ws

counter = 0


def new_player():
    global counter
    counter += 1
    user = "b" + format(int(time.time() * 10) % 10000000, "x") + format(counter, "x")
    nick = "B" + user[1:]
    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    c.recv(); c.recv()
    c.send(b"\x2d\x03" + ws(nick)); c.recv()
    uid = struct.unpack("<I", c.recv()[1:5])[0]
    c.sock.close()
    return uid, nick


def record(uid, nick, state, stamp=None):
    data = struct.pack("<II", uid, uid) + ws(nick) + bytes([state])
    return data if stamp is None else data + struct.pack("<I", stamp)


def main():
    ok = True
    a_id, a_nick = new_player()
    b_id, b_nick = new_player()
    a = ChatClient(a_id, a_nick)

    a.send(b"\x41\x27")
    got = a.recv()
    ok &= check("empty buddy list = recorded sChat 11 0000", got == bytes.fromhex("20110000"), got.hex())

    a.send(b"\x41\x28" + ws(b_nick))
    got = a.recv()
    head = b"\x20\x12" + record(b_id, b_nick, 2)
    stamp = struct.unpack("<I", got[-4:])[0]
    ok &= check("add buddy: sChat 12 = id, id, nickname, state 2, time (recorded layout)",
                got[:-4] == head and abs(stamp - time.time()) < 120, got.hex())
    got = a.recv()
    ok &= check("add buddy: sChat 15 01", got == bytes.fromhex("201501"), got.hex())

    a.send(b"\x41\x27")
    got = a.recv()
    ok &= check("buddy list = recorded layout: u16 count, 01, record",
                got == b"\x20\x11\x01\x00\x01" + record(b_id, b_nick, 2, stamp), got.hex())

    a.send(b"\x41\x28" + ws("NoSuchPlayerHere"))
    got = a.recv()
    ok &= check("add unknown nickname = recorded sChat 15 02", got == bytes.fromhex("201502"), got.hex())

    a.send(b"\x41\x28" + ws(a_nick))
    got = a.recv()
    ok &= check("adding yourself is refused (Chat_SelfBuddy, 90)", got == bytes.fromhex("20155a"), got.hex())
    a.send(b"\x41\x28" + ws(b_nick))
    got = a.recv()
    ok &= check("adding a buddy twice is refused (Chat_AlreadyBuddy, 91)", got == bytes.fromhex("20155b"), got.hex())

    b = ChatClient(b_id, b_nick)
    got = a.recv()
    ok &= check("buddy comes online: sChat 13 with state 1", got == b"\x20\x13" + record(b_id, b_nick, 1, stamp), got.hex())
    b.send(b"\x41\x27")
    got = b.recv()
    ok &= check("B: own list is empty", got == bytes.fromhex("20110000"), got.hex())
    got = b.recv()
    ok &= check("B: sChat 17 lists A as someone who added them",
                got == b"\x20\x17" + struct.pack("<I", 1) + struct.pack("<I", a_id) + ws(a_nick), got.hex())

    a.send(b"\x41\x26" + ws(b_nick) + ws("hi"))
    wanted = b"\x20\x0e" + struct.pack("<I", a_id) + ws(a_nick) + struct.pack("<I", b_id) + ws(b_nick) + ws("hi")
    got = a.recv()
    ok &= check("whisper: sender gets the recorded sChat 0E", got == wanted, got.hex())
    got = b.recv()
    ok &= check("whisper: target gets the same message", got == wanted, got.hex())

    b.send(b"\x41\x29\x01" + struct.pack("<I", a_id))
    got = b.recv()
    ok &= check("B accepts: sChat 12 with A, state 1", got[:-4] == b"\x20\x12" + record(a_id, a_nick, 1), got.hex())

    a.send(b"\x41\x2a" + struct.pack("<I", b_id))
    got = a.recv()
    ok &= check("remove buddy: sChat 14 with the record", got[:2] == b"\x20\x14" and got[2:10] == struct.pack("<II", b_id, b_id), got.hex())
    a.send(b"\x41\x27")
    got = a.recv()
    ok &= check("list is empty again", got == bytes.fromhex("20110000"), got.hex())

    a.sock.close()
    got = b.recv()
    ok &= check("buddy goes offline: sChat 13 with state 2", got[:2] == b"\x20\x13" and got[-5] == 2, got.hex())

    b.send(b"\x41\x26" + ws(a_nick) + ws("still there?"))
    got = b.recv()
    ok &= check("whisper to an offline player: a notice instead", got[:2] == b"\x20\x0e" and "is not online".encode("utf-16le") in got, got.hex()[:60])

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
