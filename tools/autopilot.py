"""Autopilot: a headless client that walks through everything the game client can ask for.

It is not the game. It speaks the protocol: registers a fresh account, visits every screen,
buys, equips, reinforces, sells, pulls a capsule, plays a single-play stage, opens the
leaderboard and records, creates a room in every mode, hosts a match, chats, uses the friend
list - and also sends every request whose purpose is still unknown. For each request it
shows what came back, and at the end it prints what the server itself logged as missing
(logs/missing-packets-*.log).

What it cannot find: a reply that is sent but wrong. Only the real client can tell that -
its log (Documents/Busters/Log) says "server message process failed [...]"; see
tools/missing_report.py, which collects those lines too.

    python tools/autopilot.py
"""
import glob
import os
import socket
import struct
import sys
import time

from test_chat_room import ChatClient
from test_client import Client, ws

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATEGORY = {
    0x00: "keepalive", 0x05: "sServer", 0x06: "sUserStart", 0x08: "sIntro", 0x09: "sUserInfo", 0x0B: "sInventory",
    0x0C: "sStore", 0x0D: "sRoomList", 0x0E: "sMode", 0x0F: "sLobby", 0x10: "sRoom", 0x12: "sSinglePlay",
    0x13: "sUserRecords", 0x14: "sHost", 0x15: "sGame", 0x17: "sUserMsg", 0x19: "sReward", 0x11: "sLadder", 0x1C: "sRank",
    0x1E: "sCapsuleMachine", 0x1F: "sCommunity", 0x20: "sChat", 0x21: "sChatRoom",
}
rows = []


def drain(client, wait=0.35):
    client.sock.settimeout(wait)
    replies = []
    try:
        while True:
            replies.append(client.recv())
    except (socket.timeout, OSError):
        pass
    return [reply for reply in replies if reply[:1] != b"\x00"]


def describe(replies):
    if not replies:
        return "-- no reply --"
    return ", ".join("%s %02X" % (CATEGORY.get(reply[0], "%02X" % reply[0]), reply[1] if len(reply) > 1 else 0) for reply in replies)


def ask(client, what, message, wait=0.35):
    client.send(message)
    replies = drain(client, wait)
    rows.append((what, message.hex()[:28], describe(replies)))
    return replies


def slot_of_added(replies):
    for reply in replies:
        if reply[:2] == b"\x0b\x01":
            return struct.unpack_from("<H", reply, 4)[0]
    return 0


def u16(value):
    return struct.pack("<H", value)


def main():
    stamp = format(int(time.time()) % 0xFFFFFF, "x")
    user, nick = "auto" + stamp, "A" + stamp
    c = Client()
    c.finish_handshake(c.login(user, "autopilot1"))
    drain(c)
    print("account %s / nickname %s" % (user, nick))

    # ---- new player
    ask(c, "check nickname", bytes.fromhex("2d02") + ws(nick))
    start = ask(c, "create nickname", bytes.fromhex("2d03") + ws(nick))
    uid = next((reply[1:5] for reply in start if reply[:1] == b"\x06"), bytes(4))
    ask(c, "create character (class 1)", bytes.fromhex("2d0401f6010400160000000000"))
    ask(c, "tutorial skipped", bytes.fromhex("2e00"))

    # ---- shop
    ask(c, "open shop", bytes.fromhex("311b"))
    buy = lambda name, item, option=1: slot_of_added(
        ask(c, "buy " + name, bytes.fromhex("301e") + struct.pack("<I", item) + bytes(7) + bytes([option, 1])))
    weapon = buy("weapon 60012", 60012)
    stone = buy("reinforce stone 251", 251)
    stone2 = buy("reinforce stone 251", 251)
    package = buy("package 5011", 5011)
    timed = buy("time-limited item 501", 501)
    changer = buy("nickname changer 305", 305)
    box = buy("lucky box 5501", 5501)
    key = buy("key 5997", 5997)
    converter = buy("converter 271", 271)
    ask(c, "buy with RT (payment 2)", bytes.fromhex("301e") + struct.pack("<I", 251) + bytes(7) + bytes([1, 2]))

    # ---- inventory (My Locker)
    ask(c, "open inventory", bytes.fromhex("311a"))
    ask(c, "equip weapon", bytes.fromhex("2f0f") + u16(weapon))
    ask(c, "reinforce (13)", bytes.fromhex("2f13") + u16(weapon) + u16(stone))
    ask(c, "reinforce with insurance (12)", bytes.fromhex("2f12") + u16(weapon) + u16(stone2) + bytes(2))
    ask(c, "convert (14)", bytes.fromhex("2f14") + u16(weapon) + u16(converter))
    ask(c, "unequip weapon", bytes.fromhex("2f10") + u16(weapon))
    ask(c, "open package (17)", bytes.fromhex("2f17") + u16(package))
    ask(c, "open lucky box with key (18)", bytes.fromhex("2f18") + u16(box) + u16(key))
    ask(c, "extend time (15)", bytes.fromhex("2f15") + u16(timed) + bytes([1, 1]))
    ask(c, "use an item that is not usable (1B)", bytes.fromhex("2f1b") + u16(stone2))
    ask(c, "extend time with RT (15)", bytes.fromhex("2f15") + u16(timed) + bytes([1, 2]))
    ask(c, "check new nickname (19)", bytes.fromhex("2f19") + ws("Z" + stamp))
    ask(c, "change nickname (1A)", bytes.fromhex("2f1a") + u16(changer) + ws("Z" + stamp))
    ask(c, "sell weapon (11)", bytes.fromhex("2f11") + u16(weapon))

    # ---- capsule machine, records, leaderboard, ladder
    ask(c, "open capsule machine", bytes.fromhex("311f"))
    for part in range(1, 7):
        ask(c, "capsule 20004 part %d" % part, bytes.fromhex("3f00244e") + bytes([part, 1]))
    ask(c, "open records", bytes.fromhex("311d"))
    ask(c, "my records", bytes.fromhex("3e01"))
    ask(c, "records of another player", bytes.fromhex("3e00") + ws("mik"))
    ask(c, "open leaderboard", bytes.fromhex("3119"))
    ask(c, "my standing", bytes.fromhex("3d0b"))
    for board in range(1, 6):
        ask(c, "leaderboard hall of fame, mode %d" % board, bytes.fromhex("3d0c01") + bytes([board]) + struct.pack("<IBI", 1, 0, 50))
    ask(c, "open ladder (ranked)", bytes.fromhex("311e"))
    ask(c, "ladder: start match (03)", bytes.fromhex("3403"))
    ask(c, "ladder: cancel match (04)", bytes.fromhex("3404"))
    ask(c, "ladder: create ladder room (05)", bytes.fromhex("340501"))
    ask(c, "back home", bytes.fromhex("3117"))

    # ---- single play
    ask(c, "open single play", bytes.fromhex("311c"), wait=2.5)
    for stage in (1, 2, 6):
        ask(c, "single stage %d: start" % stage, bytes.fromhex("3503") + u16(stage))
        ask(c, "single stage %d: loaded" % stage, bytes.fromhex("3507"))
        ask(c, "single stage %d: lost" % stage, bytes.fromhex("350500"))
        ask(c, "single stage %d: retry" % stage, bytes.fromhex("3504"))
        ask(c, "single stage %d: won" % stage, bytes.fromhex("350501"))
        ask(c, "single stage %d: exit" % stage, bytes.fromhex("3506"))

    # ---- lobby and rooms
    ask(c, "open lobby", bytes.fromhex("3118"))
    ask(c, "enter channel 1", bytes.fromhex("3203") + u16(1))
    ask(c, "refresh", bytes.fromhex("3204") + u16(1))
    ask(c, "/gm_observe room 1 (cLobby 02)", bytes.fromhex("3202") + u16(1))
    ask(c, "room details of room 1", bytes.fromhex("3205") + u16(1))
    ask(c, "join missing room", bytes.fromhex("3201") + u16(60000) + bytes(3))

    for mode, name, players in ((2, "survival", 6), (9, "BSR boss", 4), (1, "team deathmatch", 16), (8, "free for all", 16),
                                (4, "jessium", 16), (6, "SZM", 16), (7, "ZIM", 16), (10, "channel-5 team", 8)):
        created = ask(c, "create room: " + name, bytes.fromhex("3200") + ws("auto " + name) + bytes([0, 0, mode, players, 0, 0, 1, 1]))
        if not any(reply[:2] == b"\x10\x05" for reply in created):
            continue
        ask(c, "  change team", bytes.fromhex("330801"))
        ask(c, "  host ready", bytes.fromhex("330c"), wait=0.35 if mode in (2, 9) else 6)
        ask(c, "  host cancel ready", bytes.fromhex("330d"))
        if mode in (2, 9):
            play_match(c, uid, name)
        ask(c, "  leave room", bytes.fromhex("3306"))

    # ---- one room for the requests whose purpose is not known
    ask(c, "create room for unknown requests", bytes.fromhex("3200") + ws("auto misc") + bytes([0, 0, 2, 6, 0, 0, 1, 1]))
    ask(c, "room chat", bytes.fromhex("3905") + ws("hello") + bytes(2))
    ask(c, "kick a missing player", bytes.fromhex("3309") + struct.pack("<I", 1))
    ask(c, "switch to observer", bytes.fromhex("3316"))
    ask(c, "switch to player", bytes.fromhex("3317"))
    ask(c, "enter running match (cRoom 14), none running", bytes.fromhex("3314"))
    ask(c, "observe running match (cRoom 15), none running", bytes.fromhex("3315"))
    ask(c, "balance teams (cRoom 0F AdjustGame)", bytes.fromhex("330f"))
    ask(c, "observer ready (cRoom 18)", bytes.fromhex("331801"))
    ask(c, "report ping 1 ms (cRoom 0B)", bytes.fromhex("330b") + u16(1))
    ask(c, "ladder room: find match (cRoom 12)", bytes.fromhex("3312"))
    ask(c, "ladder room: cancel find match (cRoom 13)", bytes.fromhex("3313"))
    for sub in (5, 8):
        ask(c, "cGame %02X (purpose unknown)" % sub, bytes([0x37, sub]))
    ask(c, "cItem 0A", bytes.fromhex("330a01"))
    ask(c, "enter invited room 1 (cCommunity 00)", bytes([0x40, 0]) + u16(1) + u16(1) + struct.pack("<I", 0))
    ask(c, "leave room", bytes.fromhex("3306"))
    ask(c, "back home", bytes.fromhex("3117"))

    # ---- chat server: friends, search, whisper
    chat = ChatClient(struct.unpack("<I", uid)[0], nick)
    drain(chat)
    ask(chat, "chat: buddy list", bytes.fromhex("4127"))
    ask(chat, "chat: search 'm'", bytes.fromhex("412e") + ws("m"))
    ask(chat, "chat: add buddy 'mik'", bytes.fromhex("4128") + ws("mik"))
    ask(chat, "chat: join chat room 1", bytes.fromhex("4200") + u16(1))
    ask(chat, "chat: channel chat", bytes.fromhex("4124") + u16(1) + ws("hello"))
    ask(chat, "chat: whisper", bytes.fromhex("4126") + ws("mik") + ws("hi"))
    ask(chat, "chat: invite 'mik' (1D)", bytes.fromhex("411d") + struct.pack("<I", 1) + ws("mik"))
    ask(chat, "chat: automatic refusal (1E)", bytes.fromhex("411e76") + ws(nick) + ws("mik"))
    ask(chat, "chat: decline invitation (1F)", bytes.fromhex("411f") + ws("mik") + struct.pack("<IHHI", 1, 1, 1, 0) + bytes([0]))
    ask(chat, "chat: follow 'mik' (20)", bytes.fromhex("4120") + struct.pack("<I", 1) + ws("mik"))
    ask(chat, "chat: decline follow (21)", bytes.fromhex("4121") + ws("mik") + struct.pack("<I", 1) + bytes([0, 0]))

    # ---- report
    width = max(len(row[0]) for row in rows)
    print()
    for what, sent, got in rows:
        print("%-*s  %-28s  %s" % (width, what, sent, got))
    silent = [row for row in rows if row[2].startswith("--")]
    print("\n%d requests sent, %d got no reply at all" % (len(rows), len(silent)))

    print("\nwhat the servers logged as missing for this run:")
    seen = set()
    for path in glob.glob(os.path.join(ROOT, "logs", "missing-packets-*.log")):
        for line in open(path, encoding="utf-8", errors="replace"):
            if user in line or nick in line:
                text = line.split("] ", 3)[-1].strip()
                if text not in seen:
                    seen.add(text)
                    print("   " + text)
    if not seen:
        print("   (nothing)")
    return 0


def play_match(c, uid, name):
    """Hosts a match alone, the way the game's host does, and reports its end."""
    ask(c, "  host ready", bytes.fromhex("330c"))
    started = ask(c, "  start (%s)" % name, bytes.fromhex("330e"))
    game = next((reply for reply in started if reply[:2] == bytes([0x15, 0])), None)
    if game is None:
        return
    room = game[2:6]
    event = bytes([0x36, 0x0C]) + room
    ask(c, "  map loaded", bytes.fromhex("3709"))
    ask(c, "  host: created (07)", bytes([0x36, 0x07]) + room)
    ask(c, "  host: 09", bytes([0x36, 0x09]) + room + bytes(2))
    ask(c, "  host: player entered", event + bytes([0]) + uid)
    boss = "boss" in name.lower()
    for code in ((0x12, 0x13, 0x14) if boss else (0x0F, 0x10, 0x11)):
        ask(c, "  host: %s player event %02X" % ("boss" if boss else "survival", code), event + bytes([code]) + uid, wait=0.15)
    if not boss:
        ask(c, "  host: wave 1 cleared with 4 stars (ev 0E)", event + bytes([0x0E, 1, 4]), wait=0.15)
    ask(c, "  host: item used (ev 0A)", event + bytes([0x0A]) + uid + struct.pack("<H", 2), wait=0.15)
    ask(c, "  host: charger used (ev 03)", event + bytes([0x03]) + uid, wait=0.15)
    ask(c, "  host: match ended (13)", bytes([0x36, 0x13]) + room)
    ask(c, "  host: attack share and combo (ev 04)", event + bytes([4, 1]) + uid + bytes([100]) + struct.pack("<Hbb", 15, 0, 0), wait=0.15)
    ask(c, "  host: slays and revives (ev %02X)" % (7 if boss else 5), event + bytes([7 if boss else 5, 1]) + uid + struct.pack("<HB", 87, 1), wait=0.15)
    ask(c, "  host: statistics (0D)", bytes([0x36, 0x0D]) + room + bytes([1]) + uid + bytes(14 + 109))
    ask(c, "  host: closed (0A) -> result screen", bytes([0x36, 0x0A]) + room)
    ask(c, "  result screen closed (cCommand 03)", bytes([0x3B, 0x03]))


if __name__ == "__main__":
    sys.exit(main())
