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

    /// <summary>sMode 15 `u8 error` (client 0x59D765): the requested screen is refused; the client shows that error and stays.</summary>
    public static PacketWriter Refused(NetError reason) => new PacketWriter(MsgCategory.sMode, 0x15).WriteByte((byte)reason);
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

/// <summary>
/// The ladder rating as the client stores it: a mean and a deviation. The client shows
/// <c>clamp(mean - 3 * deviation, 0, 50) * 100</c> as the player's ladder points (0x851F70).
/// </summary>
public readonly record struct LadderRating(float Mean, float Deviation)
{
    private const float PointsPerUnit = 100f;
    private const float MaxUnits = 50f;

    public static LadderRating FromPoints(int points) => new(Math.Clamp(points / PointsPerUnit, 0f, MaxUnits), 0f);

    public void Write(Span<byte> target)
    {
        BitConverter.TryWriteBytes(target, Mean);
        BitConverter.TryWriteBytes(target[4..], Deviation);
    }

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[8];
        Write(bytes);
        return bytes;
    }
}

public static class UserInfoPacket
{
    public static byte ClassMask(byte characterClass) => (byte)(1 << (characterClass & 7));

    private const int ExpOffsetInSecondPart = 0;
    private const int GoldOffsetInSecondPart = 4;
    private const int CashOffsetInSecondPart = 8;

    private const int LadderRatingOffsetInSecondPart = 12;
    private const int ChannelOffsetInSecondPart = 271;
    private const int MatchingOffsetInSecondPart = 273;

    public const uint NoStigma = uint.MaxValue;

    private const int HomeStatsOffsetInSecondPart = 20;
    private const int HomeStatsSize = 86;
    private const ushort HomeStatsField = 1 << 4;
    private const int EquippedOffsetInSecondPart = 106;
    private const int FlagsOffsetInSecondPart = 226;
    private const int MissionsOffsetInSecondPart = 247;

    public const int FlagsSize = 21;
    public const int GradeFlag = 0;
    public const int GameMasterFlag = 1;

    /// <summary>Bit 0 "NEW" on My Locker, bit 1 on Single Play, bit 2 on Ranked (main frame SetNewTag, client 0x70B90B).</summary>
    public const int NewTagsFlag = 2;
    public const int CanUnlockClassFlag = 11;
    public const int MissionsSize = 24;

    private const ushort FlagsField = 1 << 6;
    private const ushort MissionsField = 1 << 7;

    public static PacketWriter FlagChanged(int index, byte value)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8).WriteUInt16(FlagsField).WriteUInt32(1u << index).WriteByte(value);

    /// <summary>
    /// Partial update of the home statistics (client 0x84CA60): `u16 86` (bits), an 11-byte mask with one bit per byte
    /// of the block, then the chosen bytes - here all of them.
    /// </summary>
    public static PacketWriter HomeStatsChanged(byte[] homeStats)
    {
        byte[] mask = new byte[(HomeStatsSize + 7) / 8];
        for (int bit = 0; bit < HomeStatsSize; bit++) mask[bit / 8] |= (byte)(1 << (bit % 8));
        return new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8).WriteUInt16(HomeStatsField)
            .WriteUInt16(HomeStatsSize).WriteBytes(mask).WriteBytes(homeStats);
    }

    public static PacketWriter MissionsChanged(byte[] missions)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8).WriteUInt16(MissionsField)
            .WriteUInt16(MissionsSize).WriteBytes([0xFF, 0xFF, 0xFF]).WriteBytes(missions);

    public static PacketWriter Full(
        string nickname,
        byte characterClass,
        byte[] classSlots,
        byte[] equippedTables,
        IPEndPoint? publicEndPoint,
        IPEndPoint? localEndPoint,
        byte level,
        byte ladderLevel,
        uint exp,
        uint gold,
        uint cash,
        LadderRating rating,
        byte classMask = 0,
        byte[]? flags = null,
        byte[]? missions = null,
        ushort channelId = 0,
        bool ladderMatching = false,
        uint stigmaUntil = NoStigma,
        byte[]? homeStats = null)
    {
        var writer = new PacketWriter(MsgCategory.sUserInfo, 0);
        WriteFirstPart(writer, nickname, characterClass, classSlots, publicEndPoint, localEndPoint, level, ladderLevel, rating, classMask, stigmaUntil);

        byte[] secondPart = (byte[])CapturedBlocks.UserInfoSecondPart.Clone();
        BitConverter.TryWriteBytes(secondPart.AsSpan(ExpOffsetInSecondPart), exp);
        BitConverter.TryWriteBytes(secondPart.AsSpan(GoldOffsetInSecondPart), gold);
        BitConverter.TryWriteBytes(secondPart.AsSpan(CashOffsetInSecondPart), cash);
        rating.Write(secondPart.AsSpan(LadderRatingOffsetInSecondPart));
        BitConverter.TryWriteBytes(secondPart.AsSpan(ChannelOffsetInSecondPart), channelId);
        secondPart[MatchingOffsetInSecondPart] = (byte)(ladderMatching ? 1 : 0);
        equippedTables.CopyTo(secondPart, EquippedOffsetInSecondPart);
        flags?.CopyTo(secondPart, FlagsOffsetInSecondPart);
        missions?.CopyTo(secondPart, MissionsOffsetInSecondPart);
        if (homeStats is { Length: HomeStatsSize }) homeStats.CopyTo(secondPart, HomeStatsOffsetInSecondPart);
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
        byte ladderLevel,
        LadderRating rating,
        byte classMask = 0,
        uint stigmaUntil = NoStigma)
    {
        writer.WriteWideString(nickname);
        writer.WriteBytes(rating.ToBytes());
        writer.WriteByte(characterClass);

        writer.WriteBytes(classSlots);

        writer.WriteUInt16(1).WriteByte(0x01);
        writer.WriteZeros(9);

        writer.WriteByte(level).WriteByte(ladderLevel).WriteByte(classMask != 0 ? classMask : ClassMask(characterClass));
        writer.WriteUInt32(stigmaUntil);
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

    private const ulong CurrentClassField = 1 << 2;
    private const ulong ProfileField = 1 << 5;
    private const byte ProfileClassMask = 1 << 2;

    public static PacketWriter CurrentClass(byte characterClass)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteUInt64(CurrentClassField).WriteByte(characterClass).WriteUInt16(0);

    public static PacketWriter OwnedClasses(byte classMask)
        => new PacketWriter(MsgCategory.sUserInfo, 1).WriteUInt64(ProfileField).WriteByte(ProfileClassMask).WriteByte(classMask).WriteUInt16(0);

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

    /// <summary>
    /// What the original server sends on every entry of the home screen (recorded as <see cref="CapturedBlocks.PartialAfterTutorial"/>
    /// for a new account): a partial with the home statistics (mask bit 4) and the daily missions (bit 7) - here with
    /// the player's own values instead of the recorded ones.
    /// </summary>
    public static PacketWriter HomeEntered(byte[] homeStats, byte[] missions)
    {
        byte[] statsMask = new byte[(HomeStatsSize + 7) / 8];
        for (int bit = 0; bit < HomeStatsSize; bit++) statsMask[bit / 8] |= (byte)(1 << (bit % 8));
        return new PacketWriter(MsgCategory.sUserInfo, 1).WriteZeros(8).WriteUInt16(HomeStatsField | MissionsField)
            .WriteUInt16(HomeStatsSize).WriteBytes(statsMask).WriteBytes(homeStats)
            .WriteUInt16(MissionsSize).WriteBytes([0xFF, 0xFF, 0xFF]).WriteBytes(missions);
    }

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

public enum ChannelStatus : byte
{
    High = 0,
    Medium = 1,
    Low = 2,
    SemiMax = 3,
    Max = 4,
}

public sealed class ChannelInfo
{
    public ChannelStatus Status { get; init; } = ChannelStatus.Low;

    public required IPEndPoint Server { get; init; }
    public required ushort Id { get; init; }
    public required string Name { get; init; }

    public byte Type { get; init; } = 4;
    public byte LevelMin { get; init; } = 1;
    public byte LevelMax { get; init; } = 0x63;
    public string Country { get; init; } = "Message0B";

    public required IPEndPoint Secondary { get; init; }

    public void Write(PacketWriter writer)
    {
        writer.WriteEndPoint(Server)
            .WriteUInt16(Id)
            .WriteWideString(Name)
            .WriteByte(Type)
            .WriteByte(LevelMin)
            .WriteByte(LevelMax)
            .WriteByte((byte)Status)
            .WriteString(Country)
            .WriteEndPoint(Secondary);
    }
}

public static class GlobalSyncPacket
{
    public const byte SeparateChannelsByCountry = 0x20;

    private const byte Full = 5;
    private const byte Partial = 6;
    private const byte FlagsField = 1;

    public static PacketWriter State(byte flags, ushort first = 0, ushort second = 0)
        => new PacketWriter(MsgCategory.sGlobalSync, Full).WriteByte((byte)(flags & 0x3F)).WriteUInt16(first).WriteUInt16(second);

    public static PacketWriter FlagsChanged(byte flags)
        => new PacketWriter(MsgCategory.sGlobalSync, Partial).WriteByte(FlagsField).WriteByte((byte)(flags & 0x3F));
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
            writer.WriteByte(update).WriteUInt16(channel.Id).WriteByte((byte)channel.Status).WriteEndPoint(channel.Server);
        return writer;
    }

    public static PacketWriter ChannelChanged(ushort channelId)
        => new PacketWriter(MsgCategory.sServer, 2).WriteUInt16(channelId);

    // sTransServer (client 0x5F50E0): 00 `u16 channel, addr server, u32 key` - the client opens a second connection
    // to that server ("PbHandshaking_C_Connect_NewLS"), sends cClientTransferInfo there and waits for sUserRestart.
    // 01 (no body) - the change failed; the client stays where it is.
    public static PacketWriter ServerChange(ushort channelId, System.Net.IPEndPoint server, uint key)
        => new PacketWriter(MsgCategory.sTransServer, 0).WriteUInt16(channelId).WriteEndPoint(server).WriteUInt32(key);

    public static PacketWriter ServerChangeFailed() => new PacketWriter(MsgCategory.sTransServer, 1);

    /// <summary>sUserRestart (client 0x59DCB0, no body): "Server change complete" - sent on the new connection instead of sUserStart.</summary>
    public static PacketWriter ServerChangeComplete() => new PacketWriter(MsgCategory.sUserRestart);
}

/// <summary>
/// sSecurity (client 0x59FCD0). With the Apex anti-cheat module loaded the client hands 00 `u16 length, bytes` to that
/// module; without it, it only reads 01 `u32 length, bytes` and writes the text to its log.
/// </summary>
public static class SecurityPacket
{
    public static PacketWriter ModuleData(ReadOnlySpan<byte> data)
        => new PacketWriter(MsgCategory.sSecurity, 0).WriteUInt16((ushort)data.Length).WriteBytes(data.ToArray());

    public static PacketWriter LogText(string text)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
        return new PacketWriter(MsgCategory.sSecurity, 1).WriteUInt32((uint)bytes.Length).WriteBytes(bytes);
    }
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

    GmObserve = 2,

    EnterChannel = 3,

    Refresh = 4,

    RoomInfo = 5,
}

/// <summary>What the lobby shows of one room.</summary>
public sealed record RoomListEntry(string Title, byte Kind, byte Players, byte MaxPlayers, byte State, ushort LevelId, System.Net.IPEndPoint Host, ushort Tail)
{
    public const byte AllOfPartOne = 0x1F;
    public const byte AllOfPartTwo = 0x07;

    public static RoomListEntry Of(Sessions.Room room)
        => new(room.Title, 8, room.PlayerCount, room.MaxPlayers, room.State, room.LevelId, room.HostEndPoint, 2);
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

    /// <summary>
    /// sLobby 01 (client 0x5F3AB8, reader 0x5F6C30): the player count of the lobby changed - `u8 mask`, and the new
    /// `u16 count` when bit 0 is set. Lets the number follow players coming and going without a refresh.
    /// </summary>
    public static PacketWriter PlayerCountChanged(ushort count) => new PacketWriter(MsgCategory.sLobby, 1).WriteByte(1).WriteUInt16(count);

    // sRoomList (client 0x5F3F70). A room is `u16 id` + two parts (readers 0x5E7D40 and 0x5E8030):
    //   part one: wstr title, u8 kind, u8 players, u8 max players, u8 state
    //   part two: u16 map, addr host, u16
    // 00 whole list, 01 one room added, 02 `u16 id` removed, 03 one room changed: each part comes with a
    // `u8` mask first (bit n = field n, readers 0x5E7DC0 and 0x5E80B0) and only the chosen fields follow.
    public static PacketWriter RoomList(IReadOnlyList<Sessions.Room> rooms)
    {
        var writer = new PacketWriter(MsgCategory.sRoomList, 0).WriteUInt16((ushort)rooms.Count);
        foreach (Sessions.Room room in rooms)
            WriteRoom(writer.WriteUInt16(room.Id), RoomListEntry.Of(room), RoomListEntry.AllOfPartOne, RoomListEntry.AllOfPartTwo, withMasks: false);
        return writer;
    }

    public static PacketWriter RoomAdded(ushort roomId, RoomListEntry room)
        => WriteRoom(new PacketWriter(MsgCategory.sRoomList, 1).WriteUInt16(roomId), room, RoomListEntry.AllOfPartOne, RoomListEntry.AllOfPartTwo, withMasks: false);

    public static PacketWriter RoomRemoved(ushort roomId) => new PacketWriter(MsgCategory.sRoomList, 2).WriteUInt16(roomId);

    public static PacketWriter RoomChanged(ushort roomId, RoomListEntry before, RoomListEntry now)
    {
        byte one = (byte)((before.Title != now.Title ? 1 : 0) | (before.Kind != now.Kind ? 2 : 0) | (before.Players != now.Players ? 4 : 0)
            | (before.MaxPlayers != now.MaxPlayers ? 8 : 0) | (before.State != now.State ? 16 : 0));
        byte two = (byte)((before.LevelId != now.LevelId ? 1 : 0) | (!before.Host.Equals(now.Host) ? 2 : 0) | (before.Tail != now.Tail ? 4 : 0));
        return WriteRoom(new PacketWriter(MsgCategory.sRoomList, 3).WriteUInt16(roomId), now, one, two, withMasks: true);
    }

    private static PacketWriter WriteRoom(PacketWriter writer, RoomListEntry room, byte one, byte two, bool withMasks)
    {
        if (withMasks) writer.WriteByte(one);
        if ((one & 1) != 0) writer.WriteWideString(room.Title);
        if ((one & 2) != 0) writer.WriteByte(room.Kind);
        if ((one & 4) != 0) writer.WriteByte(room.Players);
        if ((one & 8) != 0) writer.WriteByte(room.MaxPlayers);
        if ((one & 16) != 0) writer.WriteByte(room.State);

        if (withMasks) writer.WriteByte(two);
        if ((two & 1) != 0) writer.WriteUInt16(room.LevelId);
        if ((two & 2) != 0) writer.WriteEndPoint(room.Host);
        if ((two & 4) != 0) writer.WriteUInt16(room.Tail);
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
