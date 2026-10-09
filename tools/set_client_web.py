"""Points the client's built-in browser pages (notice panel and store home) at the emulator's web server.

The addresses are text inside the client executables: one "notice" and one "shop" address per publisher. This tool
replaces every one of them (they are "http://pbb-ext.cf/notice" and "http://pbb-ext.cf/shop" in this client pack) with
<base>/notice and <base>/shop. A new address must fit into the room of the old one; the tool says so if it does not.

The base is taken from config/emulator.json (PublicAddress and WebPort) unless given:

    python tools/set_client_web.py                         # http://<PublicAddress>:<WebPort>
    python tools/set_client_web.py http://127.0.0.1:27180
    python tools/set_client_web.py show                    # only list what the executables contain now

Every executable in ../bin whose name starts with "pbclient" is changed; the first time, each one is kept as
<name>.before-web-change.
"""
import glob
import json
import os
import re
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "..", "bin")
PAGES = (b"notice", b"shop")
ADDRESS = re.compile(rb"http://[A-Za-z0-9.\-:]+/(notice|shop)\x00")


def configured_base():
    with open(os.path.join(ROOT, "config", "emulator.json"), encoding="utf-8-sig") as handle:
        settings = json.load(handle)
    return "http://%s:%d" % (settings.get("PublicAddress", "127.0.0.1"), int(settings.get("WebPort", 27180)))


def slots(data):
    """Every notice / shop address with the room it has: its own bytes and the zero bytes behind it."""
    for match in ADDRESS.finditer(data):
        end = match.end()
        while end < len(data) and data[end] == 0:
            end += 1
        yield match.start(), match.group()[:-1], end - match.start() - 1, match.group(1)


def main():
    show = len(sys.argv) > 1 and sys.argv[1] == "show"
    base = (sys.argv[1] if len(sys.argv) > 1 and not show else configured_base()).rstrip("/").encode()
    targets = sorted(path for path in glob.glob(os.path.join(BIN, "pbclient*.exe")))
    if not targets:
        print("No pbclient*.exe in %s" % os.path.normpath(BIN))
        return 1

    failed = False
    for path in targets:
        with open(path, "rb") as handle:
            data = bytearray(handle.read())
        found = list(slots(bytes(data)))
        print("%s: %d address(es)" % (os.path.basename(path), len(found)))
        if show:
            for offset, current, room, _ in found:
                print("    0x%X  %s  (room for %d characters)" % (offset, current.decode(), room))
            continue

        changed = 0
        for offset, current, room, page in found:
            new = base + b"/" + page
            if len(new) > room:
                print("    0x%X: '%s' is %d characters, only %d fit - left as %s" % (offset, new.decode(), len(new), room, current.decode()))
                failed = True
                continue
            if new != current:
                data[offset:offset + room + 1] = new + bytes(room + 1 - len(new))
                changed += 1
        if not changed:
            print("    nothing to change")
            continue

        backup = path + ".before-web-change"
        if not os.path.exists(backup):
            shutil.copyfile(path, backup)
        try:
            with open(path, "wb") as handle:
                handle.write(data)
        except PermissionError:
            print("    could not write - is this client running? Close it and run the tool again.")
            failed = True
            continue
        print("    %d address(es) now point at %s" % (changed, base.decode()))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
