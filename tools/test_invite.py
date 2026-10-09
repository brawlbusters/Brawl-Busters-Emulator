"""Friend-list invitations and follow requests on the chat server.

    invite:  A cChat 1D (id, nick)  ->  B sChat 03 (A id, A nick, channel, room, key), A sChat 05 (124, B)
             B cChat 1F (.., accept) -> B sChat 04 (A nick, A id, channel, room, key)   - B then joins that room
             B cChat 1F (.., decline) -> A sChat 05 (122, B)
    follow:  B cChat 20 (id, nick)  ->  A sChat 06 (B id, B nick), B sChat 05 (125, A)
             A cChat 21 (B nick, B id, accept, 0) -> B sChat 07 (A nick, A id, channel, room, password)

    python tools/test_invite.py
"""
import socket
import struct
import sys
import time

from test_chat_room import ChatClient
from test_client import check, ws


def drain(client, wait=0.5):
    client.sock.settimeout(wait)
    replies = []
    try:
        while True:
            replies.append(client.recv())
    except (socket.timeout, OSError):
        pass
    return [reply for reply in replies if reply[:1] != b"\x00"]


def find(replies, sub):
    return next((reply for reply in replies if reply[:2] == bytes([0x20, sub])), b"")


def main():
    ok = True
    stamp = int(time.time()) % 100000
    a_id, b_id = 700000 + stamp, 800000 + stamp
    a_name, b_name = "InvA%d" % stamp, "InvB%d" % stamp
    a, b = ChatClient(a_id, a_name), ChatClient(b_id, b_name)
    drain(a), drain(b)
    aid, bid = struct.pack("<I", a_id), struct.pack("<I", b_id)

    a.send(bytes.fromhex("411d") + bid + ws(b_name))
    reply = find(drain(a), 5)
    ok &= check("invite without being in a room: sChat 05 + Room_NotExistInviter (115)", reply == b"\x20\x05\x73" + ws(b_name), reply.hex())

    a.send(bytes.fromhex("412f") + struct.pack("<IHH", 0x1234, 1, 7))
    drain(a)
    a.send(bytes.fromhex("411d") + struct.pack("<I", 1) + ws("nobody_here"))
    reply = find(drain(a), 5)
    ok &= check("invite someone who is offline: Chat_TagetOffLine (116)", reply[:3] == b"\x20\x05\x74", reply.hex())

    a.send(bytes.fromhex("411d") + bid + ws(b_name))
    sent, got = find(drain(a), 5), find(drain(b), 3)
    place = struct.pack("<HHI", 1, 7, 0x1234)
    ok &= check("invite: target gets sChat 03 (inviter id, nick, channel 1, room 7, key)", got == b"\x20\x03" + aid + ws(a_name) + place, got.hex())
    ok &= check("invite: inviter gets sChat 05 + Chat_SendInvitation_Result (124)", sent == b"\x20\x05\x7c" + ws(b_name), sent.hex())

    b.send(bytes.fromhex("411f") + ws(a_name) + aid + place + b"\x00")
    reply = find(drain(a), 5)
    ok &= check("decline: inviter gets Chat_Taget_Rejection (122) with the nickname", reply == b"\x20\x05\x7a" + ws(b_name), reply.hex())

    b.send(bytes.fromhex("411f") + ws(a_name) + aid + place + b"\x01")
    reply = find(drain(b), 4)
    ok &= check("accept: sChat 04 tells the invited player where to go", reply == b"\x20\x04" + ws(a_name) + aid + place, reply.hex())

    b.send(bytes.fromhex("411e") + b"\x76" + ws(b_name) + ws(a_name))
    reply = find(drain(a), 5)
    ok &= check("automatic refusal (level limit 118) reaches the inviter", reply == b"\x20\x05\x76" + ws(b_name), reply.hex())

    b.send(bytes.fromhex("4120") + aid + ws(a_name))
    sent, got = find(drain(b), 5), find(drain(a), 6)
    ok &= check("follow: target gets sChat 06 (follower id, nick)", got == b"\x20\x06" + bid + ws(b_name), got.hex())
    ok &= check("follow: follower gets Chat_SendFollow_Result (125)", sent == b"\x20\x05\x7d" + ws(a_name), sent.hex())

    a.send(bytes.fromhex("4121") + ws(b_name) + bid + b"\x01\x00")
    reply = find(drain(b), 7)
    ok &= check("follow accepted: sChat 07 (nick, id, channel, room, password)",
                reply == b"\x20\x07" + ws(a_name) + aid + struct.pack("<HH", 1, 7) + ws(""), reply.hex())
    a.send(bytes.fromhex("4121") + ws(b_name) + bid + b"\x00\x00")
    reply = find(drain(b), 5)
    ok &= check("follow declined: Chat_Taget_Rejection (122)", reply == b"\x20\x05\x7a" + ws(a_name), reply.hex())

    a.send(bytes.fromhex("411a")); drain(a)
    b.send(bytes.fromhex("4120") + aid + ws(a_name))
    reply = find(drain(b), 5)
    ok &= check("follow someone who is not in a room: Chat_Taget_Not_IngameWaitingRoom (120)", reply[:3] == b"\x20\x05\x78", reply.hex())

    from test_intrude import drain as game_drain, has, player
    host, host_id = player("vh")
    guest, guest_id = player("vg")
    host.send(bytes.fromhex("3200") + ws("invite room") + b"\x04\x00abcd" + bytes([1, 8, 0, 0, 1, 1]))
    entered = next(reply for reply in game_drain(host) if reply[:2] == b"\x10\x05")
    room = entered[3:5]
    guest.send(bytes.fromhex("3117")); game_drain(guest)
    guest.send(bytes.fromhex("4000") + struct.pack("<H", 1) + room + bytes(4))
    got = game_drain(guest)
    ok &= check("cCommunity 00 from the home screen: sCommunity 00 01, then the room (password not asked)",
                [r[:2] for r in got][:1] == [b"\x1f\x00"] and got[0][2:3] == b"\x01" and has(got, b"\x10\x05") and has(got, b"\x0e\x0c"),
                str([r.hex()[:8] for r in got]))
    game_drain(host)
    guest.send(bytes.fromhex("330b") + struct.pack("<H", 42))
    seen = next((reply for reply in game_drain(host) if reply[:2] == b"\x10\x08"), b"")
    ok &= check("cRoom 0B ping 42: the host sees the slot's ping bytes change", struct.pack("<IH", 0xC00, 42) in seen, seen.hex())
    guest.send(bytes.fromhex("4000") + struct.pack("<HHI", 1, 60000, 0))
    got = game_drain(guest)
    ok &= check("cCommunity 00 to a room that is gone: sCommunity 00 + Room_NotExistInviter (115)", has(got, b"\x1f\x00\x73"), str([r.hex()[:8] for r in got]))

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
