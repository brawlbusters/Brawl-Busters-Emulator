"""Gives ranked matches a loading screen background instead of a black one.

The loading screen (ui_gfx_pbloading) keeps one background per game mode in a table - aBGName[mode] = 'mcBG_TDM',
'mcBG_SUV', ... - and SetLevelInfo(name, mode, image) makes this[aBGName[mode]] visible. The table has no entry for
ranked play (TYPE_GAME_MODE_LADDER_TDM = 10), and every ranked room reports mode 10, so nothing is made visible and
the players and the map picture sit on black.

Ranked play is team deathmatch, so the client is made to tell the loading screen "mode 1" when the mode is 10. Only
that one call is changed (the native wrapper of SetLevelInfo at 0x703A1E); the match itself still runs as mode 10.

    0x703A4F  lea eax,[ebp+34] / push eax / lea eax,[ebp-48]      ->  jmp to new code, 2 x nop
    new code  cmp dword [ebp+34], 10 / jne +7 / mov dword [ebp+34], 1 / the three instructions / jmp 0x703A56

Every executable in ../bin whose name starts with "pbclient" is patched in place; the first time, each one is kept
as <name>.before-ranked-loading. Close the game first - a running client cannot be written.

    python tools/patch_client_ranked_loading.py
    python tools/patch_client_ranked_loading.py show      # only say which executables have the patch
    python tools/patch_client_ranked_loading.py undo      # take the patch out again

tools/patch_loading_ranked_title.py is the better fix (a real "RANKED MATCH" loading screen in the UI file); it needs
this patch to be absent, because with it the UI is never told that the match is a ranked one.
"""
import glob
import os
import shutil
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "..", "bin")

TEXT_VA, TEXT_RAW = 0x401000, 0x400            # .text: virtual address and file offset
TEXT_VIRTUAL_SIZE = 0x8D041C                   # as built
TEXT_RAW_SIZE = 0x8D0600                       # the virtual size is raised to this so the new bytes are mapped

HOOK = 0x703A4F
AFTER_HOOK = 0x703A56
ORIGINAL = bytes.fromhex("8d453450" "8d45b8")  # lea eax,[ebp+34] / push eax / lea eax,[ebp-48]
CAVE = 0xCD1480                                # zero bytes after the last instruction of .text
LADDER_MODE, TEAM_DEATHMATCH = 10, 1


def offset(va):
    return va - TEXT_VA + TEXT_RAW


def rel32(source_after, target):
    return struct.pack("<i", target - source_after)


def new_code():
    code = bytearray()
    code += bytes([0x83, 0x7D, 0x34, LADDER_MODE])                       # cmp dword [ebp+34], 10
    code += bytes([0x75, 0x07])                                          # jne over the mov
    code += bytes([0xC7, 0x45, 0x34]) + struct.pack("<I", TEAM_DEATHMATCH)   # mov dword [ebp+34], 1
    code += ORIGINAL
    code += bytes([0xE9]) + rel32(CAVE + len(code) + 5, AFTER_HOOK)      # jmp back
    return bytes(code)


def text_section(data):
    pe_header = struct.unpack_from("<I", data, 0x3C)[0]
    section_count = struct.unpack_from("<H", data, pe_header + 6)[0]
    table = pe_header + 24 + struct.unpack_from("<H", data, pe_header + 20)[0]
    for index in range(section_count):
        entry = table + index * 40
        if data[entry:entry + 5] == b".text":
            return entry
    return None


def state(data):
    """'patched', 'original' or a reason why this file is neither."""
    code, jump = new_code(), bytes([0xE9]) + rel32(HOOK + 5, CAVE) + b"\x90\x90"
    at_hook = bytes(data[offset(HOOK):offset(HOOK) + len(ORIGINAL)])
    at_cave = bytes(data[offset(CAVE):offset(CAVE) + len(code)])
    if at_hook == jump and at_cave == code:
        return "patched"
    if at_hook != ORIGINAL:
        return "not the client build this patch was made for (at %X: %s)" % (HOOK, at_hook.hex())
    if at_cave != bytes(len(code)):
        return "the room for the new code at %X is not empty" % CAVE
    return "original"


def undo(targets):
    """Takes only this patch out again (the hook and the new code); anything else done to the file stays."""
    failed = False
    for path in targets:
        name = os.path.basename(path)
        with open(path, "rb") as handle:
            data = bytearray(handle.read())
        if state(data) != "patched":
            print("%s: not patched" % name)
            continue
        data[offset(HOOK):offset(HOOK) + len(ORIGINAL)] = ORIGINAL
        data[offset(CAVE):offset(CAVE) + len(new_code())] = bytes(len(new_code()))
        try:
            with open(path, "wb") as handle:
                handle.write(data)
        except PermissionError:
            print("%s: could not write - is this client running?" % name)
            failed = True
            continue
        print("%s: patch removed" % name)
    return 1 if failed else 0


def main():
    show = len(sys.argv) > 1 and sys.argv[1] == "show"
    targets = sorted(glob.glob(os.path.join(BIN, "pbclient*.exe")))
    if len(sys.argv) > 1 and sys.argv[1] == "undo":
        return undo(targets)
    if not targets:
        print("No pbclient*.exe in %s" % os.path.normpath(BIN))
        return 1

    failed = False
    for path in targets:
        name = os.path.basename(path)
        with open(path, "rb") as handle:
            data = bytearray(handle.read())
        found = state(data)
        if show or found != "original":
            print("%s: %s" % (name, found))
            failed |= found not in ("patched", "original")
            continue

        entry = text_section(data)
        size = struct.unpack_from("<I", data, entry + 8)[0] if entry is not None else None
        if size not in (TEXT_VIRTUAL_SIZE, TEXT_RAW_SIZE):
            print("%s: unexpected .text size - left alone" % name)
            failed = True
            continue

        code = new_code()
        data[offset(CAVE):offset(CAVE) + len(code)] = code
        data[offset(HOOK):offset(HOOK) + len(ORIGINAL)] = bytes([0xE9]) + rel32(HOOK + 5, CAVE) + b"\x90\x90"
        struct.pack_into("<I", data, entry + 8, TEXT_RAW_SIZE)

        backup = path + ".before-ranked-loading"
        if not os.path.exists(backup):
            shutil.copyfile(path, backup)
        try:
            with open(path, "wb") as handle:
                handle.write(data)
        except PermissionError:
            print("%s: could not write - is this client running? Close it and run the tool again." % name)
            failed = True
            continue
        print("%s: patched" % name)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
