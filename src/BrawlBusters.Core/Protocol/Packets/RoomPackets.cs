using System.Buffers.Binary;
using System.Net;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Protocol.Packets;

public enum RoomPhase
{
    Created,

    Settings,

    Ready,

    Starting,

    Loaded,

    Playing,
}

public readonly record struct RoomSlot(int Index, uint UserId, byte CharacterClass, byte Status, byte Team = 0);

public static class RoomPacket
{
    private const int SlotSize = 26;
    private const int TeamOffset = 5;
    private const int StatusOffset = 6;
    private const byte RecordedRoomOption = 8;

    public const byte StatusWaiting = 2;

    public const byte StatusNotReady = 0;
    public const byte StatusLoaded = 3;
    public const byte StatusPlaying = 1;

    private sealed record PhaseData(byte State, int Padding);

    private static readonly Dictionary<RoomPhase, PhaseData> Phases = new()
    {
        [RoomPhase.Created] = new(0x00, 18),
        [RoomPhase.Settings] = new(0x00, 12),
        [RoomPhase.Ready] = new(0x05, 8),
        [RoomPhase.Starting] = new(0x03, 12),
        [RoomPhase.Loaded] = new(0x01, 11),
        [RoomPhase.Playing] = new(0x02, 11),
    };

    private const int EnteredPadding = 19;

    public static byte StateOf(RoomPhase phase) => Phases[phase].State;

    public static PacketWriter Entered(Room room, IPEndPoint relay, IReadOnlyList<RoomSlot> slots,
        IReadOnlyList<(uint UserId, byte[] RecordPart)> others)
    {
        var writer = new PacketWriter(MsgCategory.sRoom, 5)
            .WriteByte(0)
            .WriteUInt16(room.Id)
            .WriteWideString(room.Title)
            .WriteByte(RecordedRoomOption)
            .WriteByte(room.PlayerCount)
            .WriteByte(room.MaxPlayers)
            .WriteByte(room.State)
            .WriteUInt16(room.LevelId)
            .WriteEndPoint(room.HostEndPoint)
            .WriteUInt16(2)
            .WriteUInt32(room.HostUserId)
            .WriteByte(0);

        WriteSlotBitset(writer, slots);
        foreach (RoomSlot slot in slots.OrderBy(slot => slot.Index))
            writer.WriteBytes(SlotRecord(slot));

        writer.WriteEndPoint(relay)
            .WriteUInt32(room.HostUserId)
            .WriteUInt16(room.RuleId)
            .WriteByte((byte)others.Count);
        foreach ((uint userId, byte[] recordPart) in others)
            writer.WriteUInt32(userId).WriteBytes(recordPart);
        return writer.WriteZeros(EnteredPadding);
    }

    public static PacketWriter State(Room room, RoomPhase phase, IPEndPoint relay, IReadOnlyList<RoomSlot> slots, int actor = -1)
    {
        var writer = StateHeader(room, phase);

        if (phase == RoomPhase.Created)
        {
            writer.WriteByte(0x01);
            WriteSlotBitset(writer, slots);
            foreach (RoomSlot slot in slots.OrderBy(slot => slot.Index))
                writer.WriteBytes(SlotRecord(slot));
        }
        else
        {
            writer.WriteByte(0x04);
            WriteSlotBitset(writer, slots);
            foreach (RoomSlot slot in slots.OrderBy(slot => slot.Index))
                WriteSlotChange(writer, phase, slot, slots.Count == 1 || slot.Index == actor);
        }

        return StateFooter(writer, room, relay, Phases[phase].Padding);
    }

    public static PacketWriter SlotChanged(Room room, IPEndPoint relay, RoomSlot slot)
    {
        var writer = StateHeader(room, RoomPhase.Created, room.State);
        writer.WriteByte(0x04);
        WriteSlotBitset(writer, [slot]);
        writer.WriteUInt32((1u << TeamOffset) | (1u << StatusOffset)).WriteByte(slot.Team).WriteByte(slot.Status);
        return StateFooter(writer, room, relay, Phases[RoomPhase.Settings].Padding);
    }

    public static PacketWriter SlotRemoved(Room room, IPEndPoint relay, int removedSlot)
    {
        var writer = StateHeader(room, RoomPhase.Created);
        writer.WriteByte(0x02);
        WriteSlotBitset(writer, [new RoomSlot(removedSlot, 0, 0, 0)], onlyListed: true);
        return StateFooter(writer, room, relay, Phases[RoomPhase.Settings].Padding);
    }

    public static PacketWriter PlayerJoined(uint userId, byte[] recordPart)
        => new PacketWriter(MsgCategory.sRoom, 6).WriteUInt32(userId).WriteBytes(recordPart);

    public static PacketWriter PlayerLeft(uint userId)
        => new PacketWriter(MsgCategory.sRoom, 7).WriteUInt32(userId);

    public static PacketWriter Error(NetError error) => new PacketWriter(MsgCategory.sRoom, 9).WriteByte((byte)error);

    public static PacketWriter ReadyAccepted() => new PacketWriter(MsgCategory.sRoom, 0x0D).WriteByte(1);

    public static PacketWriter GameStarting(Room room, IPEndPoint hostLocal, IPEndPoint relay, ushort playedMapId)
        => new PacketWriter(MsgCategory.sGame, 0)
            .WriteUInt16(room.Id)
            .WriteUInt16(0)
            .WriteUInt32(room.HostUserId)
            .WriteEndPoint(room.HostEndPoint)
            .WriteEndPoint(hostLocal)
            .WriteEndPoint(relay)
            .WriteUInt16(playedMapId)
            .WriteUInt16(room.RuleId)
            .WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .WriteUInt16(0);

    public readonly record struct HostPlayerEntry(
        int Slot, uint PlayerId, string Nickname, byte CharacterClass, byte Team, byte[] ClassSlots, bool IsPlayer = true);

    public static PacketWriter HostPlayers(Room room, IReadOnlyList<HostPlayerEntry> players)
    {
        var writer = new PacketWriter(MsgCategory.sHost, 0)
            .WriteUInt16(room.Id)
            .WriteUInt16(0)
            .WriteByte(1);
        WriteSlotBitset(writer, players.Select(player => new RoomSlot(player.Slot, player.PlayerId, player.CharacterClass, 0)).ToList());
        foreach (HostPlayerEntry player in players.OrderBy(player => player.Slot))
        {
            writer.WriteUInt32(player.PlayerId)
                .WriteWideString(player.Nickname)
                .WriteByte(UserInfoPacket.ClassMask(player.CharacterClass))
                .WriteByte(player.CharacterClass)
                .WriteByte(player.Team)
                .WriteByte(0)
                .WriteBytes(player.ClassSlots)
                .WriteUInt16(0)
                .WriteBool(player.IsPlayer);
        }
        return writer.WriteUInt16(0);
    }

    public static PacketWriter Reward(uint rewardId) => new PacketWriter(MsgCategory.sReward, 0).WriteUInt32(rewardId);

    private static PacketWriter StateHeader(Room room, RoomPhase phase, byte? state = null)
        => new PacketWriter(MsgCategory.sRoom, 8)
            .WriteByte(0x1F)
            .WriteWideString(room.Title)
            .WriteByte(RecordedRoomOption)
            .WriteByte(room.PlayerCount)
            .WriteByte(room.MaxPlayers)
            .WriteByte(state ?? Phases[phase].State)
            .WriteByte(7)
            .WriteUInt16(room.LevelId)
            .WriteEndPoint(room.HostEndPoint)
            .WriteUInt16(2)
            .WriteByte(7)
            .WriteUInt32(room.HostUserId)
            .WriteByte(0);

    private static PacketWriter StateFooter(PacketWriter writer, Room room, IPEndPoint relay, int padding)
        => writer.WriteByte(7)
            .WriteEndPoint(relay)
            .WriteUInt32(room.HostUserId)
            .WriteUInt16(room.RuleId)
            .WriteZeros(padding);

    private static void WriteSlotBitset(PacketWriter writer, IReadOnlyList<RoomSlot> slots, bool onlyListed = false)
    {
        _ = onlyListed;
        int bitCount = slots.Count == 0 ? 0 : slots.Max(slot => slot.Index) + 1;
        byte[] bits = new byte[(bitCount + 7) / 8];
        foreach (RoomSlot slot in slots)
            bits[slot.Index / 8] |= (byte)(1 << (slot.Index % 8));
        writer.WriteUInt16((ushort)bitCount).WriteBytes(bits);
    }

    private static byte[] SlotRecord(RoomSlot slot)
    {
        byte[] record = new byte[SlotSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, slot.UserId);
        record[4] = slot.CharacterClass;
        record[TeamOffset] = slot.Team;
        record[StatusOffset] = slot.Status;
        return record;
    }

    private static void WriteSlotChange(PacketWriter writer, RoomPhase phase, RoomSlot slot, bool isActor)
    {
        switch (phase)
        {
            case RoomPhase.Ready:
                writer.WriteUInt32(0x0F00).WriteUInt32(0);
                break;
            case RoomPhase.Loaded when isActor:
                writer.WriteUInt32(1u << StatusOffset).WriteByte(StatusLoaded);
                break;
            case RoomPhase.Playing when isActor:
                writer.WriteUInt32(1u << StatusOffset).WriteByte(StatusPlaying);
                break;
            default:
                writer.WriteUInt32(0);
                break;
        }
    }
}
