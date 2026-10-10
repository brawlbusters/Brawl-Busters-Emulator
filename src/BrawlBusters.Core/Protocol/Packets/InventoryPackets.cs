using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Protocol.Packets;

public static class InventoryPacket
{
    public static PacketWriter List(IReadOnlyList<InventoryItem> items)
    {
        ushort nextSlot = (ushort)(items.Count == 0 ? 1 : items.Max(item => item.Slot) + 1);
        var writer = new PacketWriter(MsgCategory.sInventory, 0).WriteUInt16(nextSlot).WriteZeros(20);
        foreach (InventoryItem item in items.OrderBy(item => item.Slot))
        {
            writer.WriteUInt16(item.Slot);
            WriteItem(writer, item);
        }
        return writer;
    }

    public static PacketWriter Added(IReadOnlyList<InventoryItem> items)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 1).WriteByte((byte)items.Count);
        foreach (InventoryItem item in items)
        {
            writer.WriteByte(1).WriteUInt16(item.Slot).WriteUInt16(item.Slot);
            WriteItem(writer, item);
        }
        return writer;
    }

    public static PacketWriter Sold(bool ok, ushort slot)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 4).WriteBool(ok);
        return ok ? writer.WriteUInt16(slot).WriteBool(true) : writer;
    }

    public static PacketWriter Used(NetError result) => new PacketWriter(MsgCategory.sInventory, 5).WriteByte((byte)result);

    /// <summary>
    /// sInventory 05 with an error (client 0x5EC314): closes the "please wait" box and shows the text of that error.
    /// The general answer for an inventory request that failed for a reason the client has its own text for.
    /// </summary>
    public static PacketWriter Failed(NetError reason) => Used(reason);

    /// <summary>
    /// sInventory 02 `bool` (client 0x5EBE9E): answer to "click to activate" on an item that waited for a level.
    /// False makes the client show "Failed to activate item".
    /// </summary>
    public static PacketWriter Activated(bool ok) => new PacketWriter(MsgCategory.sInventory, 2).WriteBool(ok);

    public static PacketWriter Renamed(NetError result, string nickname)
        => new PacketWriter(MsgCategory.sInventory, 7).WriteByte((byte)result).WriteWideString(nickname);

    public static PacketWriter Reinforced(bool ok, byte outcome, ushort itemSlot, ushort stoneSlot, ushort level)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 8).WriteBool(ok);
        return ok ? writer.WriteByte(outcome).WriteUInt16(itemSlot).WriteUInt16(stoneSlot).WriteUInt16(level) : writer;
    }

    public static PacketWriter Converted(bool ok, ushort itemSlot, ushort converterSlot, ushort option)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 9).WriteBool(ok);
        return ok ? writer.WriteUInt16(itemSlot).WriteUInt16(converterSlot).WriteUInt16(option).WriteUInt16(0) : writer;
    }

    public static PacketWriter Extended(bool ok, ushort slot, byte option, uint expiry)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 0x0A).WriteBool(ok);
        return ok ? writer.WriteUInt16(slot).WriteByte(option).WriteUInt32(expiry) : writer;
    }

    /// <summary>sInventory 06 `u8 code`: raises event 30514, which the rename window shows through CheckNicknameResult(code == 1, text of the code) (client 0x5EC364, 0x70028B).</summary>
    public static PacketWriter NicknameChecked(NetError result) => new PacketWriter(MsgCategory.sInventory, 0x06).WriteByte((byte)result);

    public static PacketWriter PackageOpened(bool success)
        => new PacketWriter(MsgCategory.sInventory, 3).WriteBool(success);

    private static void WriteItem(PacketWriter writer, InventoryItem item)
    {
        writer.WriteByte(item.Type)
            .WriteUInt32(item.ItemId)
            .WriteUInt16(item.Option(2))
            .WriteUInt16(item.Option(3))
            .WriteUInt16(item.Option(4))
            .WriteUInt16(item.Quantity)
            .WriteByte(item.State)
            .WriteUInt32(item.Expiry);
    }
}

public enum InventoryRequest : byte
{
    Equip = 0x0F,

    Unequip = 0x10,

    OpenPackage = 0x17,

    OpenWithKey = 0x18,

    Sell = 0x11,

    ReinforceInsured = 0x12,

    Reinforce = 0x13,

    Convert = 0x14,

    Extend = 0x15,

    CheckNickname = 0x19,

    Rename = 0x1A,

    Use = 0x1B,
}

public static class CapsulePacket
{
    public static PacketWriter Won(ushort slot) => new PacketWriter(MsgCategory.sCapsuleMachine, 0).WriteByte(1).WriteUInt16(slot);

    /// <summary>sCapsuleMachine 01 `u8 error` (client 0x607A10): the pull failed, the client shows the text of that error.</summary>
    public static PacketWriter Error(NetError error) => new PacketWriter(MsgCategory.sCapsuleMachine, 1).WriteByte((byte)error);

    public static PacketWriter Refused() => new PacketWriter(MsgCategory.sCapsuleMachine, 0).WriteByte(0).WriteUInt16(0);
}

public enum StoreRequest : byte
{
    Buy = 0x1E,
}

public static class StorePacket
{
    public const byte Success = 0;

    public const byte Failed = 1;

    public static PacketWriter BuyResult(byte result) => new PacketWriter(MsgCategory.sStore, 0x1C).WriteByte(result);

    /// <summary>
    /// sStore 1D `u8 error` (client 0x6066E0): the purchase failed. The client closes its "please wait" box and shows the
    /// text of that error ("You have insufficient BP", ...). 1C only closes the box, whatever its byte says.
    /// </summary>
    public static PacketWriter Error(NetError error) => new PacketWriter(MsgCategory.sStore, 0x1D).WriteByte((byte)error);
}

public static class RecordsPacket
{
    private const int CommonPart = 0;
    private const int TdmPart = 268;
    private const int JesPart = 336;
    private const int SuvPart = 408;
    private const int PlacePart = 490;
    private const int SixthPartSize = 60;
    private const int SeventhPartSize = 36;

    private const int ClassBlockSize = 0x20;
    private const int ClassTriesOffset = 0x08;
    private const int ClassHitsOffset = 0x0C;
    private const int ClassKillsOffset = 0x10;
    private const int ClassAssistsOffset = 0x14;
    private const int ClassSlaysOffset = 0x18;
    private const int ClassRevivesOffset = 0x1C;
    private const int KillsOfClassOffset = 0xA0;
    private const int UserCausesOffset = 0xB4;
    private const int MobCausesOffset = 0xD4;
    private const int DeathsByClassOffset = 0xF0;
    private const int KillCauses = 7;
    private const int WinsOffset = 4;
    private const int PerfectWinsOffset = 8;
    private const int LossesOffset = 10;
    private const int PerfectLossesOffset = 14;
    private const int DrawsOffset = 16;
    private const int TeamSumsOffset = 0x14;
    private const int TdmTitlesOffset = 0x20;
    private const int JesTitlesOffset = 0x24;
    private const int TdmMarginsOffset = 0x3C;
    private const int JesMarginsOffset = 0x40;
    private const int SuvRoundsOffset = 4;
    private const int SuvLevelSize = 12;
    private const int BestSuvLevels = 0x0E;
    private const int SuvSlaysOffset = 0x28;
    private const int SuvRevivesOffset = 0x30;
    private const int SuvAttackOffset = 0x34;
    private const int SuvTitlesOffset = 0x38;
    private const int PlaceCountsOffset = 4;
    private const int PlaceSumsOffset = 0x24;
    private const int PlaceTitlesOffset = 0x30;
    private const int PlaceExtraOffset = 0x4A;

    private const int BossEntryHeader = 4;
    private const int BossRoundsOffset = 2;
    private const int BossClearsOffset = 6;
    private const int BossSumsOffset = 0x16;
    private const int BossTitlesOffset = 0x22;

    private const int BestTdm = 4;
    private const int BestJes = 9;
    private const int BestSuv = 0x17;
    private const int BestFfa = 0x1B;
    private const int BestBsr = 0x20;

    private static readonly int[] TdmTitleBits = [16, 17, 18, 5, 6, 7, 4, 3, 0, 1, 2, 10, 9, 8];
    private static readonly int[] JesTitleBits = [21, 16, 18, 5, 6, 7, 4, 3, 0, 1, 2, 10, 9, 8];
    private static readonly int[] StageTitleBits = [19, 20, 18, 5, 6, 7, 4, 3, 0, 1, 10, 9, 8];
    private static readonly int[] FfaTitleBits = [16, 17, 18, 5, 6, 7, 4, 3, 0, 1, 2, 10, 8];

    public static IReadOnlyList<int> TitleBitsOf(string key) => key switch
    {
        "jes" => JesTitleBits,
        "suv" or "bsr" => StageTitleBits,
        "ffa" => FfaTitleBits,
        _ => TdmTitleBits,
    };

    public static PacketWriter Mine(Account account) => new PacketWriter(MsgCategory.sUserRecords, 9).WriteBytes(Sheet(account));

    private static byte[] Sheet(Account account)
    {
        RecordBook book = account.Records;
        byte[] parts = new byte[PlacePart + 80];
        static void PutAt(byte[] target, int offset, int value) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset), value);
        static void ShortAt(byte[] target, int offset, int value)
            => System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset), (short)Math.Clamp(value, 0, short.MaxValue));
        void Put(int offset, int value) => PutAt(parts, offset, value);
        void PutShort(int offset, int value) => ShortAt(parts, offset, value);
        void PutTitles(byte[] target, int offset, ModeRecord record, int[] bits, params MatchMode[] modes)
        {
            for (int i = 0; i < bits.Length; i++)
            {
                int crown = bits[i] - 5;
                int count = crown is >= 0 and < RecordBook.Crowns ? modes.Sum(mode => book.CrownsOf(mode)[crown]) : record.Title(bits[i]);
                ShortAt(target, offset + i * sizeof(short), count);
            }
        }
        void PutTeamPart(int part, ModeRecord record, int sums, int titles, int[] bits, int margins, params MatchMode[] modes)
        {
            Put(part + WinsOffset, modes.Sum(mode => account.StatsOf(mode).Wins));
            PutShort(part + PerfectWinsOffset, record.PerfectWins);
            Put(part + LossesOffset, modes.Sum(mode => account.StatsOf(mode).Losses));
            PutShort(part + PerfectLossesOffset, record.PerfectLosses);
            Put(part + DrawsOffset, modes.Sum(mode => account.StatsOf(mode).Draws));
            for (int i = 0; i < sums; i++) Put(part + TeamSumsOffset + i * sizeof(int), record.Sums[i]);
            PutTitles(parts, part + titles, record, bits, modes);
            PutShort(part + margins, record.GreatWins);
            PutShort(part + margins + 2, record.GreatLosses);
            PutShort(part + margins + 4, record.CloseWins);
            PutShort(part + margins + 6, record.CloseLosses);
        }

        for (int index = 0; index < RecordBook.Classes; index++)
        {
            int at = CommonPart + index * ClassBlockSize;
            Put(at, book.ClassSeconds.ElementAtOrDefault(index));
            Put(at + ClassTriesOffset, book.ClassTries.ElementAtOrDefault(index));
            Put(at + ClassHitsOffset, book.ClassHits.ElementAtOrDefault(index));
            Put(at + ClassKillsOffset, book.ClassKills.ElementAtOrDefault(index));
            Put(at + ClassAssistsOffset, book.ClassAssists.ElementAtOrDefault(index));
            Put(at + ClassSlaysOffset, book.ClassSlays.ElementAtOrDefault(index));
            Put(at + ClassRevivesOffset, book.ClassRevives.ElementAtOrDefault(index));
        }

        // "Match-up" page (client 0x7AF352): after the five class blocks come the kills of each enemy class, the
        // kills by cause (critical, invincible, bomb, flame, traffic, hand bomb, poison) against players and against
        // zombies, and the deaths by each enemy class. The "weapons" column is the client's own kills minus these.
        for (int index = 0; index < RecordBook.Classes; index++)
        {
            Put(CommonPart + KillsOfClassOffset + index * sizeof(int), book.ClassKillsOf.ElementAtOrDefault(index));
            Put(CommonPart + DeathsByClassOffset + index * sizeof(int), book.ClassDeathsBy.ElementAtOrDefault(index));
        }
        for (int cause = 0; cause < KillCauses; cause++)
        {
            Put(CommonPart + UserCausesOffset + cause * sizeof(int), book.KillsByCause.ElementAtOrDefault(cause));
            Put(CommonPart + MobCausesOffset + cause * sizeof(int), book.MobKillsByCause.ElementAtOrDefault(cause));
        }

        ModeRecord tdm = book.ModeOf("tdm"), jes = book.ModeOf("jes"), suv = book.ModeOf("suv"), ffa = book.ModeOf("ffa"), bsr = book.ModeOf("bsr");
        PutTeamPart(TdmPart, tdm, 3, TdmTitlesOffset, TdmTitleBits, TdmMarginsOffset, MatchMode.TeamDeathmatch, MatchMode.Channel5Team);
        PutTeamPart(JesPart, jes, 4, JesTitlesOffset, JesTitleBits, JesMarginsOffset, MatchMode.Jessium);
        // Glow rush labels the four "quickly under 1 minute" (wins, losses) and "when I was the match point"
        // (wins, losses): tr_JES_FastWin .. tr_JES_MPLoss. The match point pair comes from the host's statistics.
        PutShort(JesPart + JesMarginsOffset + 4, jes.MpWins);
        PutShort(JesPart + JesMarginsOffset + 6, jes.MpLosses);

        // Zombie survival by difficulty (client 0x7AC95B): three blocks of `u32 rounds, clears, stars` for rookie,
        // regular and veteran. Rounds played before the difficulty was recorded stay with rookie.
        MatchStats survival = account.StatsOf(MatchMode.Survival);
        int oldRounds = Math.Max(0, survival.Matches - book.SurvivalRounds.Sum());
        int oldClears = Math.Max(0, survival.Wins - book.SurvivalClears.Sum());
        for (int level = 0; level < RecordBook.SurvivalLevels; level++)
        {
            int at = SuvPart + SuvRoundsOffset + level * SuvLevelSize;
            Put(at, book.SurvivalRounds.ElementAtOrDefault(level) + (level == 0 ? oldRounds : 0));
            Put(at + 4, book.SurvivalClears.ElementAtOrDefault(level) + (level == 0 ? oldClears : 0));
            Put(at + 8, book.SurvivalStars.ElementAtOrDefault(level));
        }
        Put(SuvPart + SuvSlaysOffset, suv.Sums[0]);
        Put(SuvPart + SuvRevivesOffset, suv.Sums[1]);
        Put(SuvPart + SuvAttackOffset, suv.Sums[2]);
        PutTitles(parts, SuvPart + SuvTitlesOffset, suv, StageTitleBits, MatchMode.Survival);

        for (int place = 0; place < RecordBook.Places; place++)
            Put(PlacePart + PlaceCountsOffset + place * sizeof(int), book.Placements.ElementAtOrDefault(place));
        for (int i = 0; i < 3; i++) Put(PlacePart + PlaceSumsOffset + i * sizeof(int), ffa.Sums[i]);
        PutTitles(parts, PlacePart + PlaceTitlesOffset, ffa, FfaTitleBits, MatchMode.FreeForAll);
        for (int i = 0; i < 3; i++) PutShort(PlacePart + PlaceExtraOffset + i * sizeof(short), ffa.Extra[i]);

        var sheet = new PacketWriter();
        sheet.WriteBytes(parts);

        MatchStats boss = account.StatsOf(MatchMode.Bsr);
        byte[] bossPart = new byte[SixthPartSize];
        PutAt(bossPart, BossRoundsOffset, boss.Matches);
        for (int grade = 0; grade < RecordBook.BossGrades; grade++)
            PutAt(bossPart, BossClearsOffset + grade * sizeof(int), book.BossClears.ElementAtOrDefault(grade));
        for (int i = 0; i < 3; i++) PutAt(bossPart, BossSumsOffset + i * sizeof(int), bsr.Sums[i]);
        PutTitles(bossPart, BossTitlesOffset, bsr, StageTitleBits, MatchMode.Bsr);

        if (boss.Matches == 0) sheet.WriteUInt16(0);
        else sheet.WriteUInt16(1).WriteByte(1).WriteZeros(BossEntryHeader).WriteBytes(bossPart);
        sheet.WriteBytes(bossPart);

        byte[] best = new byte[SeventhPartSize];
        static byte Small(int value) => (byte)Math.Clamp(value, 0, sbyte.MaxValue);
        best[BestTdm] = Small(tdm.BestWinStreak);
        best[BestTdm + 1] = Small(tdm.BestLoseStreak);
        for (int i = 0; i < 3; i++) best[BestTdm + 2 + i] = Small(tdm.Best[i]);
        best[BestJes] = Small(jes.BestWinStreak);
        best[BestJes + 1] = Small(jes.BestLoseStreak);
        for (int i = 0; i < 3; i++) best[BestJes + 2 + i] = Small(jes.Best[i]);
        for (int level = 0; level < RecordBook.SurvivalLevels; level++)
        {
            // `u8 most stars in a round, i16 fastest clear in seconds` per difficulty (0x7AD13B, 0x7AC96E).
            best[BestSuvLevels + level * 3] = Small(book.SurvivalStarMax.ElementAtOrDefault(level));
            ShortAt(best, BestSuvLevels + level * 3 + 1, book.SurvivalBestTime.ElementAtOrDefault(level));
        }
        ShortAt(best, BestSuv, suv.Best[0]);
        best[BestSuv + 2] = Small(suv.Best[1]);
        best[BestSuv + 3] = Small(suv.Best[2]);
        ShortAt(best, BestFfa, ffa.Best[0]);
        ShortAt(best, BestFfa + 2, ffa.Best[1]);
        best[BestFfa + 4] = Small(ffa.Best[2]);
        ShortAt(best, BestBsr, bsr.Best[0]);
        best[BestBsr + 2] = Small(bsr.Best[1]);
        best[BestBsr + 3] = Small(bsr.Best[2]);
        sheet.WriteBytes(best);

        // Ninth part (client 0x857230, shown by 0x7B0702 as tr_SPPractice<stage>_Clear / _AVGTime): a slot list -
        // `u16 slots, bitset`, then per slot `u16 stage, i16 clears, i16 seconds of those clears` (the client divides).
        // A stage cleared before the clears were counted is listed with one clear and no time.
        List<KeyValuePair<ushort, int[]>> stages = book.SingleStages.Where(stage => stage.Value is { Length: 2 } && stage.Value[0] > 0)
            .Concat(account.ClearedStages().Where(stage => !book.SingleStages.ContainsKey(stage))
                .Select(stage => new KeyValuePair<ushort, int[]>(stage, [1, 0])))
            .OrderBy(stage => stage.Key).ToList();
        sheet.WriteUInt16((ushort)stages.Count);
        byte[] slots = new byte[(stages.Count + 7) / 8];
        for (int i = 0; i < stages.Count; i++) slots[i / 8] |= (byte)(1 << (i % 8));
        sheet.WriteBytes(slots);
        foreach ((ushort stage, int[] record) in stages)
        {
            // Both are signed 16-bit: keep the average right when the total time no longer fits.
            int clears = Math.Min(record[0], short.MaxValue);
            int average = record[1] / record[0];
            int seconds = (int)Math.Min((long)average * clears, short.MaxValue);
            sheet.WriteUInt16(stage).WriteUInt16((ushort)clears).WriteUInt16((ushort)seconds);
        }
        return sheet.ToArray();
    }

    public static PacketWriter End() => Result(NetError.Success);

    public static PacketWriter Result(NetError result) => new PacketWriter(MsgCategory.sUserRecords, 0x0C).WriteByte((byte)result);

    /// <summary>
    /// sUserRecords 0D `u8 level, u8 ladder level`: the client keeps the two bytes (0x600F32) and files them with the
    /// next sheet of another player (0B, 0x600DAD) - without it that player is shown with level 0.
    /// </summary>
    public static PacketWriter LevelsOfPlayer(Account account)
        => new PacketWriter(MsgCategory.sUserRecords, 0x0D).WriteByte(account.DisplayLevel).WriteByte(account.GemRank);

    public static PacketWriter OfPlayer(Account account)
        => new PacketWriter(MsgCategory.sUserRecords, 0x0B).WriteWideString(account.Nickname).WriteBytes(Sheet(account));
}
