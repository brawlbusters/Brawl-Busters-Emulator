import struct
import sys

from test_chat_room import ChatClient
from test_client import check, ws
from test_dev import newest_account, set_grade
from test_shop import new_player_at_home

CHANNEL = 1


def lobby_chat(text):
    return bytes.fromhex("4124") + struct.pack("<H", CHANNEL) + ws(text)


def main():
    ok = True
    game = new_player_at_home()
    account = newest_account()
    user_id, nickname, login_id = account["Id"], account["Nickname"], account["LoginId"]

    chat = ChatClient(user_id, nickname)
    chat.send(bytes.fromhex("4200") + struct.pack("<H", CHANNEL))
    chat.recv()

    chat.send(lobby_chat("/notice hello"))
    ok &= check("player: /notice is shown as ordinary chat",
                chat.recv() == bytes.fromhex("200d0000") + ws(nickname) + ws("/notice hello"))

    chat.send(lobby_chat("/join 3"))
    message = chat.recv()
    gm_header = bytes.fromhex("200e") + bytes(4) + ws("#GMMessage") + struct.pack("<I", user_id) + ws(nickname)
    ok &= check("/join N -> private message from #GMMessage", message.startswith(gm_header), message.hex()[:60])

    set_grade(login_id, 3)

    chat.send(lobby_chat("/notice Maintenance at 10"))
    ok &= check("developer: /notice reaches the game session as a system message",
                game.recv() == bytes.fromhex("1704") + ws("Maintenance at 10"))

    chat.send(lobby_chat("/gold 5000"))
    ok &= check("developer: /gold 5000 in lobby chat -> BP 65000 pushed to the game session",
                game.recv() == bytes.fromhex("0901") + bytes(8) + bytes.fromhex("0300") + struct.pack("<II", 0, 65000))

    chat.send(lobby_chat("/exp 200"))
    ok &= check("developer: /exp 200 in lobby chat -> experience 200 pushed",
                game.recv() == bytes.fromhex("0901") + bytes(8) + bytes.fromhex("0300") + struct.pack("<II", 200, 65000))

    chat.send(lobby_chat("/gm Hello everyone"))
    message = chat.recv()
    ok &= check("developer: /gm text -> GM message to everyone in chat",
                message.startswith(gm_header) and message.endswith(ws("Hello everyone")), message.hex()[:60])

    saved = newest_account()
    ok &= check("saved: BP 65000, experience 200, level 2",
                (saved["Gold"], saved["experience"], saved["level"]) == (65000, 200, 2))

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
