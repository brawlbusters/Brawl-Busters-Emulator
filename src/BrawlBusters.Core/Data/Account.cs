using System.Text.Json.Serialization;

namespace BrawlBusters.Core.Data;

public sealed class Account
{
    public uint Id { get; set; }
    public string LoginId { get; set; } = "";
    public string PasswordHash { get; set; } = "";

    public string Nickname { get; set; } = "";

    public CharacterShape? Character { get; set; }

    public int Gold { get; set; } = 60000;

    public int Cash { get; set; }

    [JsonPropertyName("grade")]
    public AccountGrade Grade { get; set; } = AccountGrade.Player;

    [JsonPropertyName("level")]
    public byte Level { get; set; }

    [JsonPropertyName("experience")]
    public int Experience { get; set; }

    [JsonPropertyName("gem_rank")]
    public byte GemRank { get; set; }

    [JsonPropertyName("gem")]
    public int Gem { get; set; }

    [JsonIgnore]
    public byte DisplayLevel => Math.Max((byte)1, Level);

    public List<InventoryItem> Items { get; set; } = [];

    [JsonPropertyName("equipped")]
    public ushort[][] Equipped { get; set; } = [];

    public ushort[] EquippedOf(int classIndex)
    {
        if (Equipped.Length != Loadout.ClassCount || Equipped.Any(row => row is null || row.Length != ItemType.EquipTableSize))
        {
            var table = new ushort[Loadout.ClassCount][];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = new ushort[ItemType.EquipTableSize];
                if (i < Equipped.Length && Equipped[i] is { } old)
                    Array.Copy(old, table[i], Math.Min(old.Length, table[i].Length));
            }
            Equipped = table;
        }
        return Equipped[classIndex];
    }

    public ushort FreeSlot()
    {
        ushort slot = 1;
        while (Items.Any(item => item.Slot == slot)) slot++;
        return slot;
    }

    [JsonPropertyName("buddies")]
    public List<BuddyEntry> Buddies { get; set; } = [];

    [JsonPropertyName("buddy_requests")]
    public List<uint> BuddyRequests { get; set; } = [];

    [JsonPropertyName("single_cleared")]
    public List<ushort> SingleCleared { get; set; } = [];

    public byte SingleProgressA { get; set; } = 1;
    public byte SingleProgressB { get; set; }

    public List<ushort> ClearedStages()
    {
        if (SingleCleared.Count == 0 && SingleProgressB != 0)
        {
            for (ushort stage = 0; stage < 8; stage++)
                if ((SingleProgressB & (1 << stage)) != 0) SingleCleared.Add(stage);
        }
        SingleProgressA = 1;
        SingleProgressB = 0;
        return SingleCleared;
    }

    public bool TutorialDone { get; set; }

    public ulong SessionKey { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginUtc { get; set; }

    [JsonIgnore]
    public bool HasNickname => Nickname.Length > 0;

    [JsonIgnore]
    public bool HasCharacter => Character is not null;
}

public enum AccountGrade
{
    Player = 0,
    Moderator = 1,
    GameMaster = 2,
    Developer = 3,
}

public sealed class BuddyEntry
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = "";

    [JsonPropertyName("added")]
    public uint AddedUnix { get; set; }
}

public sealed class InventoryItem
{
    public ushort Slot { get; set; }
    public uint ItemId { get; set; }

    public byte Type { get; set; }

    [JsonPropertyName("opt")]
    public ushort[] Options { get; set; } = [0, 0, 0, 0];

    public ushort Option(int number) => number >= 1 && number <= Options.Length ? Options[number - 1] : (ushort)0;

    public ushort Quantity { get; set; } = 1;

    public byte State { get; set; } = 1;

    public uint Expiry { get; set; } = uint.MaxValue;
}

public sealed class CharacterShape
{
    public byte Class { get; set; }

    public ushort[] Values { get; set; } = [];
}
