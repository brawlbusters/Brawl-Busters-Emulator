"""Slash commands typed in lobby chat (chat server): refused for a player, run for a developer, one check per command.

    python tools/test_lobby_commands.py      (needs the MariaDB backend; uses the local server under database/server)
"""
import socket
import struct
import sys
import time

from test_chat_room import ChatClient
from test_client import check, ws
from test_grade import sql, texts
from test_intrude import drain, has, player

CHANNEL = 1
GM_SENDER = "#GMMessage"


def lobby_chat(text):
    return bytes.fromhex("4124") + struct.pack("<H", CHANNEL) + ws(text)


def read_ws(message, offset):
    length, = struct.unpack_from("<H", message, offset)
    end = offset + 2 + 2 * length
    return message[offset + 2:end].decode("utf-16le"), end


def chat_drain(chat, wait=1.0):
    chat.sock.settimeout(wait)
    messages = []
    try:
        while True:
            messages.append(chat.recv())
    except (socket.timeout, OSError):
        pass
    return messages


def gm_lines(messages):
    """The texts of the private messages from #GMMessage (sChat 0E) - how a command answers in chat."""
    lines = []
    for message in messages:
        if message[:2] != b"\x20\x0e":
            continue
        sender, offset = read_ws(message, 6)
        _, offset = read_ws(message, offset + 4)
        if sender == GM_SENDER:
            lines.append(read_ws(message, offset)[0])
    return lines


def main():
    ok = True
    game, uid = player("lc")
    user_id, = struct.unpack("<I", uid)
    time.sleep(2.5)
    nickname = sql("SELECT nickname FROM users WHERE id = %d" % user_id)

    chat = ChatClient(user_id, nickname)
    chat.send(bytes.fromhex("4200") + struct.pack("<H", CHANNEL))
    chat_drain(chat)

    def say(text, wait=1.0):
        chat.send(lobby_chat(text))
        return chat_drain(chat, wait)

    # ---- as a player
    replies = say("/nosuchcommand 3")
    ok &= check("an unknown command is ordinary chat",
                bytes.fromhex("200d0000") + ws(nickname) + ws("/nosuchcommand 3") in replies, str([r.hex()[:16] for r in replies]))
    lines = gm_lines(say("/notice hello"))
    ok &= check("player: /notice is refused with the grade it needs", any("not allowed" in line and "/notice" in line for line in lines), str(lines))
    lines = gm_lines(say("/help"))
    ok &= check("player: /help says there are no commands for this grade", len(lines) == 1 and "Player" in lines[0], str(lines))

    # ---- as a developer
    sql("UPDATE users SET grade = 3 WHERE id = %d" % user_id)
    drain(game, 5.0)
    chat_drain(chat)

    lines = gm_lines(say("/help", 1.5))
    listed = [name for name in ("ban", "cash", "exp", "gold", "grade", "item", "kick", "level", "log", "notice", "observe", "online", "unban")
              if any(line.startswith("/" + name) for line in lines)]
    ok &= check("developer: /help lists all 13 other commands", len(listed) == 13, str(listed))

    say("/notice Maintenance at 10")
    ok &= check("/notice reaches the game session as a system message", "Maintenance at 10" in texts(drain(game)))
    say("/gm Hello everyone")
    ok &= check("/gm is the same command (alias)", "Hello everyone" in texts(drain(game)))

    lines = gm_lines(say("/gold 5000"))
    ok &= check("/gold 5000: answered, and the new BP pushed to the game session (sUserInfo 01)",
                any("+5000 BP" in line for line in lines) and has(drain(game), b"\x09\x01"), str(lines))
    lines = gm_lines(say("/cash 10"))
    ok &= check("/cash 10", any("+10 RT" in line for line in lines), str(lines))
    lines = gm_lines(say("/exp 200"))
    ok &= check("/exp 200", any("+200 exp" in line for line in lines), str(lines))
    lines = gm_lines(say("/gold"))
    ok &= check("/gold without an amount shows its usage", any(line.startswith("Usage: /gold") for line in lines), str(lines))
    lines = gm_lines(say("/level 5"))
    ok &= check("/level 5", any("now level 5" in line for line in lines), str(lines))
    lines = gm_lines(say("/level 500"))
    ok &= check("/level 500 is out of range", any("out of range" in line for line in lines), str(lines))
    lines = gm_lines(say("/item 0"))
    ok &= check("/item with an unknown item", any("No such player or item" in line for line in lines), str(lines))

    lines = gm_lines(say("/online", 1.5))
    ok &= check("/online counts the players and names this one",
                any("real player(s) connected" in line for line in lines) and any(line.startswith(nickname) for line in lines), str(lines[:3]))
    lines = gm_lines(say("/who", 1.5))
    ok &= check("/who is the same command (alias)", any("real player(s) connected" in line for line in lines))
    lines = gm_lines(say("/log"))
    ok &= check("/log lists the log channels", any(" on" in line or " off" in line for line in lines), str(lines)[:80])
    lines = gm_lines(say("/observe abc"))
    ok &= check("/observe without a room number shows its usage", any(line.startswith("Usage: /observe") for line in lines), str(lines))

    lines = gm_lines(say("/kick nobody_by_this_name"))
    ok &= check("/kick of somebody who is not online", any("not online" in line for line in lines), str(lines))
    lines = gm_lines(say("/ban nobody_by_this_name 5"))
    ok &= check("/ban of an unknown player", any("No such player" in line for line in lines), str(lines))
    lines = gm_lines(say("/unban nobody_by_this_name"))
    ok &= check("/unban of an unknown player", any("No such player" in line for line in lines), str(lines))
    lines = gm_lines(say("/grade %s player" % nickname))
    ok &= check("/grade sets the own grade back to player", any("is now Player" in line for line in lines), str(lines))
    lines = gm_lines(say("/gold 5"))
    ok &= check("... after which /gold is refused again", any("not allowed" in line for line in lines), str(lines))

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
