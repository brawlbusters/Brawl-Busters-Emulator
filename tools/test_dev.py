import json
import os
import struct
import sys

from test_client import check
from test_shop import expect, new_player_at_home

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ACCOUNTS = os.path.join(ROOT, "data", "accounts.json")


def newest_account():
    return json.load(open(ACCOUNTS, encoding="utf-8"))[-1]


def set_grade(login_id, grade):
    accounts = json.load(open(ACCOUNTS, encoding="utf-8"))
    for account in accounts:
        if account["LoginId"] == login_id:
            account["grade"] = grade
    json.dump(accounts, open(ACCOUNTS, "w", encoding="utf-8"), indent=2)


def dev_command(command, amount):
    return bytes([0x3C, command]) + struct.pack("<I", amount)


def main():
    ok = True
    c = new_player_at_home()
    login_id = newest_account()["LoginId"]

    c.send(dev_command(1, 5000))
    c.send(bytes.fromhex("0001"))
    ok &= expect(c, "player grade: /gold is ignored (next reply is the keep-alive)", "0000")
    ok &= check("player grade: BP unchanged in the account file", newest_account()["Gold"] == 60000)

    set_grade(login_id, 3)
    c.send(dev_command(1, 5000))
    ok &= expect(c, "developer: /gold 5000 -> BP 65000", "0901" + "00" * 8 + "0300" + "00000000" + "e8fd0000")
    c.send(dev_command(0, 200))
    ok &= expect(c, "developer: /exp 200 -> experience 200", "0901" + "00" * 8 + "0300" + "c8000000" + "e8fd0000")

    saved = newest_account()
    ok &= check("saved: BP 65000, experience 200, level 2 (150 needed)",
                (saved["Gold"], saved["experience"], saved["level"]) == (65000, 200, 2),
                str((saved["Gold"], saved["experience"], saved["level"])))

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
