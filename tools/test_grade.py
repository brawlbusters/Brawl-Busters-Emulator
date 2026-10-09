"""A grade edited in the users table reaches the logged-in player within seconds, with a system message.

    python tools/test_grade.py      (needs the MariaDB backend; uses the local server under database/server)
"""
import glob
import os
import struct
import subprocess
import sys
import time

from test_client import check, ws
from test_intrude import drain, has, player

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sql(statement):
    binary = glob.glob(os.path.join(ROOT, "database", "server", "mariadb-*-winx64", "bin", "mariadb.exe"))[-1]
    password = open(os.path.join(ROOT, "database", "server", "root-password.txt"), encoding="utf-8").read().strip()
    return subprocess.run([binary, "-h", "127.0.0.1", "-u", "root", "rockserver", "-N", "-e", statement],
                          env=dict(os.environ, MYSQL_PWD=password), capture_output=True, text=True).stdout.strip()


def texts(replies):
    out = []
    for reply in replies:
        if reply[:2] == b"\x17\x04":
            length, = struct.unpack_from("<H", reply, 2)
            out.append(reply[4:4 + 2 * length].decode("utf-16le"))
    return out


def say(client, text):
    client.send(bytes.fromhex("3905") + ws(text) + bytes(2))
    return texts(drain(client, 1.0))


def main():
    ok = True
    c, uid = player("gr")
    user = struct.unpack("<I", uid)[0]
    time.sleep(2.5)
    ok &= check("the account is saved as a player (grade 0)", sql("SELECT grade FROM users WHERE id = %d" % user) == "0")
    c.send(bytes.fromhex("3200") + ws("grade test") + bytes([0, 0, 2, 4, 0, 0, 1, 1])); drain(c)
    lines = say(c, "/help")
    ok &= check("/help as a player: told the grade and that there are no commands", len(lines) == 1 and "Player" in lines[0], str(lines))
    ok &= check("/gold as a player is refused", any("requires the grade Developer" in line for line in say(c, "/gold 5")))

    sql("UPDATE users SET grade = 3 WHERE id = %d" % user)
    replies = drain(c, 5.0)
    lines = texts(replies)
    ok &= check("grade set to 3 in the database: the player is told within seconds", any("Developer" in line for line in lines), str(lines))
    ok &= check("the game client gets its game-master flag switched on (sUserInfo 01)", has(replies, b"\x09\x01"))
    lines = say(c, "/help")
    ok &= check("/help now lists the staff commands", any("/gold" in line for line in lines) and any("/observe" in line for line in lines), str(len(lines)))
    ok &= check("/gold 5 now works", any("+5" in line for line in say(c, "/gold 5")))
    lines = say(c, "/grade %s player" % ("GR" + "x"))
    ok &= check("/grade with an unknown player shows the usage", any("Usage" in line for line in lines), str(lines))

    sql("UPDATE users SET grade = 0 WHERE id = %d" % user)
    lines = texts(drain(c, 5.0))
    ok &= check("grade set back to 0: told again", any("Player" in line for line in lines), str(lines))
    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
