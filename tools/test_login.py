"""Login and registration rules, each answered with the client's own error code (the client shows the dialog).

    sCreateIDFailed (02) u8 error    a new id or its password is refused
    sLoginFailed    (01) u8 error    an existing id: 19 wrong password, 20 third wrong password (then disconnected),
                                      16 + u32 seconds when the account is banned
    sError 00       u8 14            sent to the older connection when the same account logs in again

    python tools/test_login.py
"""
import socket
import sys
import time

from test_client import Client, check

ID_EXCEED_CHAR_COUNT, ID_PROHIBITED, ID_ANOTHER_LOGIN = 10, 12, 14
PW_WRONG, PW_THREE_TIMES, PW_SAME_AS_ID, PW_ALL_SAME, PW_LINEAR = 19, 20, 23, 24, 25


def refused(login_id, password):
    client = Client()
    reply = client.login(login_id, password)
    client.sock.close()
    return tuple(reply[:2])


def main():
    ok = True
    stamp = format(int(time.time() * 10) % 0xFFFF, "x")
    new_id = ("lg" + stamp)[:10]

    ok &= check("password equal to the login id -> 'You cannot use your LoginID as your password' (23)",
                refused(new_id, new_id) == (2, PW_SAME_AS_ID), str(refused(new_id, new_id)))
    ok &= check("the same, written in capitals", refused(new_id, new_id.upper()) == (2, PW_SAME_AS_ID))
    ok &= check("password of one repeated character (24)", refused(new_id, "zzzzzz") == (2, PW_ALL_SAME))
    ok &= check("password that is a plain series, 'abcdef' and '654321' (25)",
                refused(new_id, "abcdef") == (2, PW_LINEAR) and refused(new_id, "654321") == (2, PW_LINEAR))
    ok &= check("login id with the same character three times in a row (10)", refused("baaad" + stamp[:3], "good1pass") == (2, ID_EXCEED_CHAR_COUNT))
    ok &= check("login id that is too short (7) and one with a symbol (9)",
                refused("ab", "good1pass") == (2, 7) and refused("ab_cd", "good1pass") == (2, 9))

    first = Client()
    reply = first.login(new_id, "good1pass")
    ok &= check("a proper id and password registers and logs in (sSessionInfo)", reply[0] == 3, reply.hex()[:8])
    first.finish_handshake(reply)

    wrong = Client()
    codes = [tuple(wrong.login(new_id, "nope%d" % attempt)[:2]) for attempt in range(3)]
    ok &= check("wrong password twice gives 19, the third time 20", codes == [(1, PW_WRONG), (1, PW_WRONG), (1, PW_THREE_TIMES)], str(codes))
    wrong.sock.settimeout(3)
    try:
        closed = wrong.sock.recv(16) == b""
    except (ConnectionError, socket.timeout, OSError):
        closed = True
    ok &= check("after the third wrong password the connection is closed", closed)

    second = Client()
    second.finish_handshake(second.login(new_id, "good1pass"))
    first.sock.settimeout(3)
    told = False
    try:
        for _ in range(40):
            if first.recv() == bytes([0x1B, 0, ID_ANOTHER_LOGIN]):
                told = True
                break
    except (ConnectionError, socket.timeout, OSError, AssertionError):
        pass
    ok &= check("logging in a second time: the first connection is told 'This ID has connected twice' (sError 00 0E)", told)

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
