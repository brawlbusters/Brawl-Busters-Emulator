using System.Net;
using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Protocol.Packets;

public static class UserStartPacket
{
    public static PacketWriter Build(uint userId, string loginId, string nickname)
        => new PacketWriter(MsgCategory.sUserStart)
            .WriteUInt32(userId)
            .WriteString(loginId)
            .WriteWideString(nickname)
            .WriteUInt64(0)
            .WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}

public enum GameMode : byte
{
    Intro = 0x00,
    Tutorial = 0x01,
    Home = 0x02,
    Lobby = 0x03,

    Ranking = 0x04,

    InventoryState = 0x05,

    Inventory = 0x06,
    Store = 0x07,
    SingleLobby = 0x08,
    SingleGame = 0x09,
    Records = 0x0A,

    Ladder = 0x0B,
    WaitingRoom = 0x0C,

    WaitingRoomLadderSingle = 0x0D,
    WaitingRoomLadderMulti = 0x0E,

    ReadyRoom = 0x0F,

    LoadingGame = 0x10,

    InGame = 0x11,

    GameResult = 0x12,

    GmObserver = 0x13,
    CapsuleMachine = 0x14,
}

public static class ModePacket
{
    public static PacketWriter Build(GameMode mode) => new PacketWriter(MsgCategory.sMode, (byte)mode);
}

public static class KeepAlivePacket
{
    public static PacketWriter Idle() => new PacketWriter(MsgCategory.Start).WriteBool(false);
}

public enum IntroRequest : byte
{
    Unknown1 = 1,

    CheckNickname = 2,

    CreateNickname = 3,

    CreateCharacter = 4,
}

public static class IntroPacket
{
    public static PacketWriter CheckNicknameResult(NetError result)
        => new PacketWriter(MsgCategory.sIntro, 0).WriteByte((byte)result);

    public static PacketWriter CreateNicknameResult(NetError result)
        => new PacketWriter(MsgCategory.sIntro, 1).WriteByte((byte)result);
}

public static class UserInfoPacket
{
    public static byte ClassMask(byte characterClass) => (byte)(1 << (characterClass & 7));

    private const int ExpOffsetInSecondPart = 0;
    private const int GoldOffsetInSecondPart = 4;
    private const int CashOffsetInSecondPart = 8;

    private const int GemOffsetInSecondPart = 12;

    private const int EquippedOffsetInSecondPart = 106;

    public static PacketWriter Full(
        string nickname,
        byte characterClass,
        byte[] classSlots,
        byte[] equippedTables,
        IPEndPoint? publicEndPoint,
        IPEndPoint? localEndPoint,
        byte level,
        byte gemRank,
        uint exp,
        uint gold,
        uint cash,
        uint gem)
    {
        var writer = new PacketWriter(MsgCategory.sUserInfo, 0);
        WriteFirstPart(writer, nickname, characterClass, classSlots, publicEndPoint, localEndPoint, level, gemRank);

        byte[] secondPart = (byte[])CapturedBlocks.UserInfoSecondPart.Clone();
        BitConverter.TryWriteBytes(secondPart.AsSpan(ExpOffsetInSecondPart), exp);
        BitConverter.TryWriteBytes(secondPart.AsSpan(GoldOffsetInSecondPart), gold);
        BitConverter.TryWriteBytes(secondPart.AsSpan(CashOffsetInSecondPart), cash);
        BitConverter.TryWriteBytes(secondPart.AsSpan(GemOffsetInSecondPart), gem);
        equippedTables.CopyTo(secondPart, EquippedOffsetInSecondPart);
        writer.WriteBytes(secondPart);
        return writer;
    }

    public static void WriteFirstPart(
        PacketWriter writer,
        string nickname,
        byte characterClass,
        byte[] classSlots,
        IPEndPoint? publicEndPoint,
        IPEndPoint? localEndPoint,
        byte level,
        byte gemRank)
    {
        writer.WriteWideString(nickname);
        writer.WriteZeros(8);
        writer.WriteByte(characterClass);

        writer.WriteBytes(classSlots);

        writer.WriteUInt16(1).WriteByte(0x01);
        writer.WriteZeros(9);

        writer.WriteByte(level).WriteByte(gemRank).WriteByte(ClassMask(characterClass));
        writer.WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.WriteWideString("");
        writer.WriteUInt16(0);
        writer.WriteBool(false);

        WriteOptionalEndPoint(writer, publicEndPoint);
        WriteOptionalEndPoint(writer, localEndPoint);
    }

    private static void WriteOptionalEndPoint(PacketWriter writer, IPEndPoint? endPoint)
    {
        if (endPoint is null) writer.WriteZeros(6);
        else writer.WriteEndPoint(endPoint);
    }

    public static PacketWriter Gold(uint gold)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8).WriteUInt16(2).WriteUInt32(gold);

    public static PacketWriter Level(byte level)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteUInt64(1 << 5).WriteByte(1).WriteByte(level).WriteUInt16(0);

    public static PacketWriter Nickname(string nickname)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteUInt64(1).WriteWideString(nickname).WriteUInt16(0);

    public static PacketWriter Cash(uint cash)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8).WriteUInt16(4).WriteUInt32(cash);

    public static PacketWriter ExpAndGold(uint exp, uint gold)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8).WriteUInt16(3).WriteUInt32(exp).WriteUInt32(gold);

    public static PacketWriter PartialEmpty()
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(10);

    public static PacketWriter PartialAfterTutorial()
        => new PacketWriter(MsgCategory.sUserInfo).WriteBytes(CapturedBlocks.PartialAfterTutorial);

    public static PacketWriter PartialOnLobby(byte[] equippedTables)
    {
        var writer = new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8);
        WriteEquippedTables(writer, equippedTables);
        return writer;
    }

    public static PacketWriter Equipment(int classIndex, byte[] classSlot, byte[] equippedTables)
    {
        var writer = new PacketWriter(MsgCategory.sUserInfo, 1)
            .WriteUInt64(1 << 3)
            .WriteByte((byte)(1 << classIndex))
            .WriteUInt16((ushort)classSlot.Length)
            .WriteBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x3F])
            .WriteBytes(classSlot);
        WriteEquippedTables(writer, equippedTables);
        return writer;
    }

    private static void WriteEquippedTables(PacketWriter writer, byte[] equippedTables)
    {
        const int tableSize = 24;
        writer.WriteUInt16(1 << 5).WriteByte(0x1F);
        for (int offset = 0; offset < equippedTables.Length; offset += tableSize)
            writer.WriteBytes([0xFF, 0xFF, 0xFF, 0x00]).WriteBytes(equippedTables.AsSpan(offset, tableSize));
    }
}

public sealed class ChannelInfo
{
    public required IPEndPoint Server { get; init; }
    public required ushort Id { get; init; }
    public required string Name { get; init; }

    public byte Flag1 { get; init; } = 4;
    public byte Flag2 { get; init; } = 1;
    public byte Flag3 { get; init; } = 0x63;
    public byte Flag4 { get; init; } = 2;
    public string Text { get; init; } = "Message0B";

    public required IPEndPoint Secondary { get; init; }

    public void Write(PacketWriter writer)
    {
        writer.WriteEndPoint(Server)
            .WriteUInt16(Id)
            .WriteWideString(Name)
            .WriteByte(Flag1)
            .WriteByte(Flag2)
            .WriteByte(Flag3)
            .WriteByte(Flag4)
            .WriteString(Text)
            .WriteEndPoint(Secondary);
    }
}

public static class ServerPacket
{
    public static PacketWriter ChannelList(IReadOnlyList<ChannelInfo> channels)
    {
        var writer = new PacketWriter(MsgCategory.sServer, 0).WriteByte((byte)channels.Count);
        foreach (ChannelInfo channel in channels) channel.Write(writer);
        return writer;
    }

    public static PacketWriter ChannelStates(IReadOnlyList<ChannelInfo> channels)
    {
        const byte update = 2;
        var writer = new PacketWriter(MsgCategory.sServer, 1).WriteUInt16((ushort)channels.Count);
        foreach (ChannelInfo channel in channels)
            writer.WriteByte(update).WriteUInt16(channel.Id).WriteByte(2).WriteEndPoint(channel.Server);
        return writer;
    }

    public static PacketWriter ChannelChanged(ushort channelId)
        => new PacketWriter(MsgCategory.sServer, 2).WriteUInt16(channelId);
}

public enum ModeRequest : byte
{
    EnterHome = 0x17,

    EnterLobby = 0x18,

    EnterRanking = 0x19,

    EnterInventory = 0x1A,

    EnterStore = 0x1B,

    EnterSingleLobby = 0x1C,

    EnterRecords = 0x1D,

    EnterLadder = 0x1E,

    EnterCapsuleMachine = 0x1F,
}

public enum SinglePlayRequest : byte
{
    Start = 3,

    Retry = 4,

    Finished = 5,

    Exit = 6,

    Loaded = 7,
}

public enum LobbyRequest : byte
{
    CreateRoom = 0,

    JoinRoom = 1,

    Unknown2 = 2,

    EnterChannel = 3,

    Refresh = 4,

    RoomInfo = 5,
}

public static class LobbyPacket
{
    public static PacketWriter Opened() => new PacketWriter(MsgCategory.sLobby, 4);

    public static PacketWriter Error(NetError error) => new PacketWriter(MsgCategory.sLobby, 2).WriteByte((byte)error);

    public static PacketWriter RoomInfo(Sessions.Room room, IReadOnlyList<(byte Team, string Nickname)> players)
    {
        var writer = new PacketWriter(MsgCategory.sLobby, 3)
            .WriteUInt16(room.Id)
            .WriteUInt16(room.LevelId)
            .WriteUInt16(room.RuleId)
            .WriteUInt16(0)
            .WriteByte((byte)players.Count);
        foreach ((byte team, string nickname) in players)
            writer.WriteByte(team).WriteWideString(nickname);
        return writer;
    }

    public static PacketWriter PlayerCount(ushort count) => new PacketWriter(MsgCategory.sLobby, 0).WriteUInt16(count);

    public static PacketWriter RoomList(IReadOnlyList<Sessions.Room> rooms)
    {
        var writer = new PacketWriter(MsgCategory.sRoomList, 0).WriteUInt16((ushort)rooms.Count);
        foreach (Sessions.Room room in rooms)
        {
            writer.WriteUInt16(room.Id)
                .WriteWideString(room.Title)
                .WriteByte(8)
                .WriteByte(room.PlayerCount)
                .WriteByte(room.MaxPlayers)
                .WriteByte(room.State)
                .WriteUInt16(room.LevelId)
                .WriteEndPoint(room.HostEndPoint)
                .WriteUInt16(2);
        }
        return writer;
    }

    public static PacketWriter SinglePlayState(IReadOnlyCollection<ushort> clearedStages)
    {
        int bitCount = clearedStages.Count == 0 ? 1 : clearedStages.Max() + 1;
        byte[] bits = new byte[(bitCount + 7) / 8];
        foreach (ushort stage in clearedStages)
            bits[stage / 8] |= (byte)(1 << (stage % 8));
        return new PacketWriter(MsgCategory.sSinglePlay, 0).WriteUInt16((ushort)bitCount).WriteBytes(bits);
    }

    public static PacketWriter SinglePlayError(byte code) => new PacketWriter(MsgCategory.sSinglePlay, 2).WriteByte(code);

    public static PacketWriter SinglePlayStart(ushort stage)
        => new PacketWriter(MsgCategory.sSinglePlay, 1).WriteUInt16(stage).WriteByte(1);
}
