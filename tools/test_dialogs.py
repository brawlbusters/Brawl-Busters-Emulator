"""Refusals that the client shows as its own dialog (values of the client's NetError table).

    sLobby 02  u8 error    75 Lobby_ChangeChannel: the channel does not exist or is not open to this player
                           77 / 78 Lobby_ChannelTooLowLevel / TooHighLevel: the level is outside the channel's range
    sRoom 09   u8 error    83 Room_ExceedMaxChatLength: the chat line is too long

    python tools/test_dialogs.py
"""
import struct
import sys

from test_client import check, ws
from test_intrude import drain, first_channel, player

LOBBY_CHANGE_CHANNEL = 75
LOBBY_CHANNEL_TOO_HIGH_LEVEL = 78
ROOM_EXCEED_MAX_CHAT_LENGTH = 83
ROOKIE_CHANNEL = 7201


def main():
    ok = True
    c, _ = player("dg")

    c.send(bytes.fromhex("3203") + struct.pack("<H", 9999))
    replies = drain(c, 1.5)
    ok &= check("a channel that does not exist is refused with sLobby 02 Lobby_ChangeChannel (75)",
                replies == [bytes([0x0F, 2, LOBBY_CHANGE_CHANNEL])], str([reply.hex() for reply in replies]))

    c.send(bytes.fromhex("3203") + struct.pack("<H", first_channel()))
    replies = drain(c, 1.5)
    ok &= check("the player is still in the old channel and can enter a real one",
                not any(reply[:2] == bytes([0x0F, 2]) for reply in replies), str([reply.hex()[:12] for reply in replies]))

    c.send(bytes.fromhex("3203") + struct.pack("<H", ROOKIE_CHANNEL))
    replies = drain(c, 1.5)
    ok &= check("a level 30 player is kept out of the Rookie channel (levels 1-5): sLobby 02 Lobby_ChannelTooHighLevel (78)",
                replies == [bytes([0x0F, 2, LOBBY_CHANNEL_TOO_HIGH_LEVEL])], str([reply.hex() for reply in replies]))

    c.send(bytes.fromhex("3200") + ws("chat") + bytes([0, 0, 2, 6, 0, 0, 1, 1]))
    drain(c)
    c.send(bytes.fromhex("3905") + ws("x" * 300) + bytes(1))
    replies = drain(c, 1.5)
    ok &= check("a room chat line of 300 characters: sRoom 09 Room_ExceedMaxChatLength (83)",
                bytes([0x10, 9, ROOM_EXCEED_MAX_CHAT_LENGTH]) in replies, str([reply.hex()[:20] for reply in replies]))

    c.send(bytes.fromhex("2f11") + struct.pack("<H", 999))
    replies = drain(c, 1.5)
    ok &= check("selling a slot that holds nothing: sInventory 05 AlreadyResaleItem (107)",
                bytes([0x0B, 5, 107]) in replies, str([reply.hex()[:20] for reply in replies]))

    print("ALL PASSED" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
