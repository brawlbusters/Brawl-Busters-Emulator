import sys

from test_client import check, ws
from test_dev import newest_account, set_grade
from test_shop import expect, new_player_at_home

ROOM_CHAT = bytes.fromhex("3905")
SYSTEM_MESSAGE = bytes.fromhex("1704")


def room_chat(text):
    return ROOM_CHAT + ws(text) + bytes(2)


def main():
    ok = True
    other = new_player_at_home()
    staff = new_player_at_home()
    staff_login = newest_account()["LoginId"]
    staff_id = newest_account()["Id"]

    staff.send(room_chat("hi"))
    expected = bytes.fromhex("1700") + staff_id.to_bytes(4, "little") + ws("hi") + bytes(2)
    ok &= check("room chat echoed as recorded", staff.recv() == expected)

    staff.send(room_chat("/notice hello"))
    message = staff.recv()
    ok &= check("/notice from a player is not a system message", message[:2] == bytes.fromhex("1700"), message.hex()[:20])

    set_grade(staff_login, 2)
    staff.send(room_chat("/notice Server restarts in 5 minutes"))
    wanted = SYSTEM_MESSAGE + ws("Server restarts in 5 minutes")
    ok &= check("sender receives the system message", staff.recv() == wanted)
    ok &= check("another connected player receives it too", other.recv() == wanted)

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
