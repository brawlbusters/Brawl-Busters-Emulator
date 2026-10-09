"""Makes a copy of the client that returns to the login box after "Lost connection to game server" instead of ending.

What the client does on its own (network manager, object with the connection at +0x2C):
  * 0x436FDA  the "disconnected" callback clears the connection (and dword [ebx+2C], 0);
  * 0x59C437 / 0x4370F8  it shows error 45 (ServerDisconnected) with the "exit when closed" flag set;
  * 0x43612D  the routine that shows the login box at start-up. It only needs "not connecting, no connection" - which
    is exactly the state after a disconnect - and the login button then opens a new connection as on a fresh start.

The copy
  1. shows that error without the exit flag (two `push 1` -> `push 0`), so OK only closes the dialog;
  2. right after the connection is cleared, puts the screen state machine back into its root state (1, the connect /
     login screen - the same call the sMode handler uses) and calls the start-up login routine (a jump to a few new
     instructions in the unused bytes at the end of the code section).

The original bin/pbclient.exe is not touched; the patched copy is bin/pbclient_stay.exe (play-relogin.bat starts it).

    python tools/patch_client_relogin.py
"""
import os
import shutil
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "..", "bin", "pbclient.exe")
TARGET = os.path.join(ROOT, "..", "bin", "pbclient_stay.exe")

IMAGE_BASE = 0x400000
TEXT_VA, TEXT_RAW = 0x401000, 0x400            # .text: virtual address and file offset
TEXT_VIRTUAL_SIZE_FIELD_VALUE = 0x8D041C       # as built; raised to the raw size so the new bytes are surely mapped
TEXT_RAW_SIZE = 0x8D0600

SHOW_LOGIN = 0x43612D
SET_STATE = 0x5611F0                           # eax = state id, ecx = dispatcher (what the sMode handler calls)
CLEAR_CONNECTION = 0x436FDA                    # and dword [ebx+2C],0 / mov byte [esp+8C],0   (12 bytes)
AFTER_CLEAR = 0x436FE6
CAVE = 0xCD1420                                # zero bytes after the last instruction of .text

EXIT_FLAGS = (0x59C437, 0x4370F8)              # push 1 (exit when the dialog closes) before show_error(45, ..)


def offset(va):
    return va - TEXT_VA + TEXT_RAW


def rel32(source_after, target):
    return struct.pack("<i", target - source_after)


def main():
    with open(SOURCE, "rb") as handle:
        data = bytearray(handle.read())

    expected = {
        CLEAR_CONNECTION: bytes.fromhex("83632c00c684248c00000000"),
        EXIT_FLAGS[0]: bytes.fromhex("6a01"),
        EXIT_FLAGS[1]: bytes.fromhex("6a01"),
        CAVE: bytes(32),
    }
    for va, original in expected.items():
        found = bytes(data[offset(va):offset(va) + len(original)])
        if found != original:
            print("This is not the client build the patch was made for (at %X: %s) - nothing written." % (va, found.hex()))
            return 1

    # 1. the error dialog no longer ends the game
    for va in EXIT_FLAGS:
        data[offset(va) + 1] = 0

    # 2. new code: the two original instructions with the login routine in between, then back
    cave = bytearray()
    cave += bytes.fromhex("83632c00")                      # and dword [ebx+2C], 0
    cave += bytes.fromhex("b801000000")                    # mov eax, 1          (state 1 = PbDispatcher_C_Connect_LS, the login screen)
    cave += bytes.fromhex("8b4b38")                        # mov ecx, [ebx+38]   (the dispatcher)
    cave += bytes.fromhex("85c9")                          # test ecx, ecx
    cave += bytes.fromhex("7405")                          # jz over the call
    cave += bytes([0xE8]) + rel32(CAVE + len(cave) + 5, SET_STATE)    # call set-state: leaves the lobby / room / match screen
    cave += bytes([0x53])                                  # push ebx            (the network manager)
    cave += bytes([0xE8]) + rel32(CAVE + len(cave) + 5, SHOW_LOGIN)   # call show-login (removes its argument itself)
    cave += bytes.fromhex("c684248c00000000")              # mov byte [esp+8C], 0
    cave += bytes([0xE9]) + rel32(CAVE + len(cave) + 5, AFTER_CLEAR)  # jmp back
    data[offset(CAVE):offset(CAVE) + len(cave)] = cave

    jump = bytes([0xE9]) + rel32(CLEAR_CONNECTION + 5, CAVE) + bytes([0x90]) * 7
    data[offset(CLEAR_CONNECTION):offset(CLEAR_CONNECTION) + len(jump)] = jump

    # the section header of .text: make its virtual size cover the new bytes
    pe_header = struct.unpack_from("<I", data, 0x3C)[0]
    section_count = struct.unpack_from("<H", data, pe_header + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe_header + 20)[0]
    table = pe_header + 24 + optional_size
    for index in range(section_count):
        entry = table + index * 40
        if data[entry:entry + 5] == b".text":
            if struct.unpack_from("<I", data, entry + 8)[0] != TEXT_VIRTUAL_SIZE_FIELD_VALUE:
                print("Unexpected .text size - nothing written.")
                return 1
            struct.pack_into("<I", data, entry + 8, TEXT_RAW_SIZE)

    with open(TARGET, "wb") as handle:
        handle.write(data)
    shutil.copystat(SOURCE, TARGET)
    print("Wrote %s" % os.path.normpath(TARGET))
    return 0


if __name__ == "__main__":
    sys.exit(main())
