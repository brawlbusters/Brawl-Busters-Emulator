import json
import os
import re
import struct
import sys
import time

from test_client import Client, check, ws

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CAPTURE = os.path.join(ROOT, "logs", "capture-20261007-211238.log")
CATEGORY = {"sMode": 0x0E, "sInventory": 0x0B, "sStore": 0x0C, "sUserInfo": 0x09, "sCapsuleMachine": 0x1E}
ITEM_SIZE = 18
WEEK = 604800


def recorded(sequence, name):
    pattern = re.compile(r"\S+ \[L1\] S->C seq\s+%d %s\s+([0-9a-f]+)$" % (sequence, name))
    for line in open(CAPTURE, encoding="utf-8"):
        match = pattern.match(line.rstrip("\n"))
        if match:
            return bytes([CATEGORY[name]]) + bytes.fromhex(match.group(1))
    raise KeyError((sequence, name))


def class5_player_at_home():
    user = "i" + format(int(time.time() * 10) % 100000000, "x")[:8]
    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    c.recv(); c.recv()
    c.send(b"\x2d\x03" + ws("N" + user[1:9])); c.recv(); c.recv()
    c.send(bytes.fromhex("2d0405f9010100340000000000"))
    c.recv(); c.recv()
    c.send(bytes.fromhex("2e00"))
    for _ in range(5):
        c.recv()
    return c, user


def items_of(message, kind):
    if kind == "list":
        return list(range(24 + 2, len(message), 2 + ITEM_SIZE))
    return list(range(3 + 5, len(message), 5 + ITEM_SIZE))


def main():
    if not os.path.exists(CAPTURE):
        print("capture file missing - nothing to compare against")
        return 1
    ok = True
    c, user = class5_player_at_home()

    def same(label, sequence, name, kind=None, mask_option=False, expiry=None):
        nonlocal ok
        got, want = bytearray(c.recv()), bytearray(recorded(sequence, name))
        detail = ""
        if kind and len(got) == len(want):
            now = time.time()
            for offset in items_of(got, kind):
                stamp = struct.unpack_from("<I", got, offset + 14)[0]
                wanted_stamp = struct.unpack_from("<I", want, offset + 14)[0]
                if wanted_stamp != 0xFFFFFFFF:
                    if expiry and not (now + expiry - 60 < stamp < now + expiry + 60):
                        detail += " expiry %d is not now + %d" % (stamp, expiry)
                    got[offset + 14:offset + 18] = want[offset + 14:offset + 18] = bytes(4)
                if mask_option:
                    if not 10 <= struct.unpack_from("<H", got, offset + 7)[0] <= 17:
                        detail += " rolled reinforcement out of range"
                    got[offset + 7:offset + 9] = want[offset + 7:offset + 9] = bytes(2)
        good = got == want and not detail
        if got != want:
            detail += "\n     got  %s\n     want %s" % (bytes(got).hex(), bytes(want).hex())
        ok &= check("%s = recorded %s #%d" % (label, name, sequence), good, detail)

    c.send(bytes.fromhex("311b"))
    same("open shop", 20, "sMode")
    same("open shop: empty inventory", 21, "sInventory")

    c.send(bytes.fromhex("301e97130000000000000000000101"))
    same("buy package 5015: result", 22, "sStore")
    same("buy package 5015: BP", 23, "sUserInfo")
    same("buy package 5015: item", 24, "sInventory", "added")

    c.send(bytes.fromhex("2f170100"))
    same("open package: 6 items, first in the package slot", 26, "sInventory", "added", expiry=WEEK)
    same("open package: result", 27, "sInventory")
    same("open package: whole inventory", 28, "sInventory", "list", expiry=WEEK)

    c.send(bytes.fromhex("301efb000000000000000000000101"))
    same("buy item 251: result", 29, "sStore")
    same("buy item 251: BP", 30, "sUserInfo")
    same("buy item 251: item in slot 7", 31, "sInventory", "added")

    c.send(bytes.fromhex("311a"))
    same("open inventory", 32, "sMode")
    same("open inventory: empty update", 33, "sUserInfo")

    c.send(bytes.fromhex("2f0f0100"))
    same("wear helmet from slot 1: class slot + equipped table", 35, "sUserInfo")
    c.send(bytes.fromhex("2f100100"))
    same("take helmet off: back to the default helmet", 36, "sUserInfo")

    c.send(bytes.fromhex("311f"))
    same("open capsule machine", 41, "sMode")
    same("open capsule machine: inventory", 42, "sInventory", "list", expiry=WEEK)

    c.send(bytes.fromhex("3f00244e0101"))
    same("capsule pull 1: BP -6000", 44, "sUserInfo")
    same("capsule pull 1: weapon 62493 in slot 8", 45, "sInventory", "added", mask_option=True)
    same("capsule pull 1: result", 46, "sCapsuleMachine")
    c.send(bytes.fromhex("3f00244e0101"))
    same("capsule pull 2: BP -6000", 47, "sUserInfo")
    same("capsule pull 2: weapon 62493 in slot 9", 48, "sInventory", "added", mask_option=True)
    same("capsule pull 2: result", 49, "sCapsuleMachine")

    c.send(bytes.fromhex("3f00244e0101"))
    got = c.recv()
    ok &= check("capsule pull without enough BP is refused: sCapsuleMachine 01 Store_NoGold (55)", got == bytes.fromhex("1e0137"), got.hex())

    c.send(bytes.fromhex("2f0f0800"))
    got = c.recv()
    slot = got[21:83]
    weapon = struct.unpack_from("<4H", slot, 10)
    ok &= check("wear capsule weapon: id 62493 with its conversions 15121 / 15511",
                weapon[0] == 62493 and 10 <= weapon[1] <= 17 and weapon[2:] == (15121, 15511), str(weapon))
    table = got[83 + 3 + 4 * 28 + 4:][:24]
    ok &= check("wear capsule weapon: equipped table entry 1 = slot 8", struct.unpack_from("<12H", table)[1] == 8, table.hex())

    c.send(bytes.fromhex("2f0f0700"))
    got = c.recv()
    ok &= check("an item that cannot be worn changes nothing", struct.unpack_from("<H", got, 31)[0] == 62493, got[21:41].hex())

    c.send(bytes.fromhex("2f170700"))
    got = c.recv()
    ok &= check("opening something that is not a package is refused", got == bytes.fromhex("0b0300"), got.hex())

    c.sock.close()
    time.sleep(0.3)
    c = Client()
    c.finish_handshake(c.login(user, "pass1234"))
    record = None
    for _ in range(8):
        message = c.recv()
        if message[:2] == b"\x09\x00":
            record = message
            break
    found = record is not None and struct.pack("<4H", 62493, weapon[1], 15121, 15511) in record
    ok &= check("next login: the player record carries the worn weapon", found)
    if record is not None:
        tables = record[-274 + 106:][:120]
        ok &= check("next login: equipped table of class 5 has slot 8 for weapons",
                    struct.unpack_from("<12H", tables, 4 * 24)[1] == 8, tables[96:].hex())

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
