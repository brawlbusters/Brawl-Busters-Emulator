"""Points a server group of the client at another address.

The client reads its servers from the SERVER_ADDR table of clientconfigdb.xml inside Data/xmandb.bus and picks the row
named by its -ServerGroup argument. This tool rewrites one row in place. The file inside the archive must keep its
size, so the new text may not be longer than the old one; what is shorter is filled up with blanks after the closing tag.
The untouched archive is kept once as xmandb.bus.before-server-change.

    python tools/set_client_server.py <group> <lobby list> <chat address> [new group name of the same length]

    python tools/set_client_server.py DEV_Daniel "127.0.0.1:27100;127.0.0.1:27110" 127.0.0.1:27900
    python tools/set_client_server.py list
"""
import os
import re
import shutil
import sys

from bus_tool import read_archive, xor

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "..", "Data", "xmandb.bus")
TABLE = "clientconfigdb.xml"


def load():
    data, _, entries = read_archive(ARCHIVE)
    for name, offset, size, encrypted in entries:
        if os.path.basename(name) == TABLE:
            payload = data[offset:offset + size]
            return data, offset, size, encrypted, (xor(payload) if encrypted else payload)
    raise SystemExit("%s is not in %s" % (TABLE, ARCHIVE))


def rows(xml):
    for block in re.finditer(rb"<DATA>.*?</DATA>", xml, re.S):
        group = re.search(rb"<ID>\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</ID>", block.group(), re.S)
        if group and b"<ADDR_LIST>" in block.group():
            yield group.group(1).strip(), block


def value(block, tag):
    found = re.search(rb"<" + tag + rb">\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</" + tag + rb">", block, re.S)
    return found.group(1).strip().decode() if found else ""


def replace(block, tag, text):
    """Puts the text into the tag. What it is shorter than the old value is added as blanks after the closing tag."""
    found = re.search(rb"(<" + tag + rb">\s*(?:<!\[CDATA\[)?)(.*?)((?:\]\]>)?\s*</" + tag + rb">)", block, re.S)
    if not found:
        raise SystemExit("no <%s> in that row" % tag.decode())
    room = len(found.group(2))
    if len(text) > room:
        raise SystemExit("'%s' is %d characters, the row only has room for %d" % (text, len(text), room))
    new = found.group(1) + text.encode() + found.group(3) + b" " * (room - len(text))
    return block[:found.start()] + new + block[found.end():]


def main():
    data, offset, size, encrypted, xml = load()

    if len(sys.argv) < 2 or sys.argv[1] == "list":
        for group, block in rows(xml):
            print("%-16s %-70s chat %s" % (group.decode(), value(block.group(), b"ADDR_LIST"), value(block.group(), b"ADDR_CHAT_LIST")))
        return 0

    if len(sys.argv) < 4:
        print(__doc__)
        return 1

    wanted, lobby, chat = sys.argv[1].encode(), sys.argv[2], sys.argv[3]
    rename = sys.argv[4] if len(sys.argv) > 4 else None
    for group, block in rows(xml):
        if group != wanted:
            continue
        changed = replace(replace(block.group(), b"ADDR_LIST", lobby), b"ADDR_CHAT_LIST", chat)
        if rename:
            if len(rename) != len(wanted):
                raise SystemExit("the new name must have %d characters like '%s'" % (len(wanted), wanted.decode()))
            changed = changed.replace(wanted, rename.encode(), 1)
        patched = xml[:block.start()] + changed + xml[block.end():]
        assert len(patched) == size, (len(patched), size)

        backup = ARCHIVE + ".before-server-change"
        if not os.path.exists(backup):
            shutil.copyfile(ARCHIVE, backup)
        payload = xor(patched) if encrypted else patched
        with open(ARCHIVE, "wb") as handle:
            handle.write(data[:offset] + payload + data[offset + size:])
        print("%s -> %s: lobby %s, chat %s" % (wanted.decode(), rename or wanted.decode(), lobby, chat))
        return 0

    raise SystemExit("no server group '%s' (see: python tools/set_client_server.py list)" % wanted.decode())


if __name__ == "__main__":
    sys.exit(main())
