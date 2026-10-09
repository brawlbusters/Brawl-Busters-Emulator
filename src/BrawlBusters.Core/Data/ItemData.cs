using System.Text.Json.Serialization;

namespace BrawlBusters.Core.Data;

public static class ItemType
{
    public const byte Weapon = 1;
    public const byte Helmet = 2;
    public const byte Upper = 3;
    public const byte Hand = 4;
    public const byte Lower = 5;
    public const byte Foot = 6;
    public const byte Cloak = 7;
    public const byte Glasses = 8;
    public const byte Mask = 9;
    public const byte Decal = 10;

    public const byte UpperAlt = 11;

    public const byte WeaponReinforce = 14;
    public const byte CostumeReinforce = 15;
    public const byte WeaponPerk = 16;
    public const byte Converter = 17;
    public const byte JackpotTicket = 13;
    public const byte ClassUnlock = 18;
    public const byte SlotChanger = 23;
    public const byte GoldPack = 19;
    public const byte NicknameChanger = 27;

    public const byte Package = 24;

    public const byte LuckyBox = 25;
    public const byte LuckyBoxAlt = 26;

    public const int EquipTableSize = 12;
}

/// <param name="MinLevel">MINIMUM_LEVEL of the item table: "Requires Level N or higher". Below it the item stays inactive.</param>
public readonly record struct ItemInfo(byte Type, byte Class, bool Stackable, ushort ConvertR, ushort ConvertLR, byte MinLevel)
{
    public static ItemInfo FromArray(int[] values) => new(
        (byte)Get(values, 0), (byte)Get(values, 1), Get(values, 2) != 0, (ushort)Get(values, 3), (ushort)Get(values, 4),
        (byte)Get(values, 5));

    private static int Get(int[] values, int index) => index < values.Length ? values[index] : 0;
}

public sealed class PackageInfo
{
    [JsonPropertyName("fixed")]
    public List<uint> Fixed { get; set; } = [];

    [JsonPropertyName("prob")]
    public List<int> Prob { get; set; } = [];

    [JsonPropertyName("jackpot")]
    public List<int> Jackpot { get; set; } = [];

    [JsonPropertyName("key")]
    public uint Key { get; set; }

    [JsonIgnore]
    public bool IsLuckyBox => Prob.Count > 1 || (Prob.Count == 1 && Prob[0] > 0);
}

public sealed class FixedItem
{
    [JsonPropertyName("item")]
    public uint ItemId { get; set; }

    [JsonPropertyName("type")]
    public byte Type { get; set; }

    [JsonPropertyName("options")]
    public ushort[] Options { get; set; } = [0, 0, 0, 0];

    [JsonPropertyName("count")]
    public ushort Count { get; set; } = 1;

    [JsonPropertyName("expire")]
    public int Expire { get; set; } = -1;

    [JsonPropertyName("state")]
    public byte State { get; set; } = 1;
}

public sealed class UpgradeTable
{
    [JsonPropertyName("addon")]
    public List<int> Addon { get; set; } = [];

    [JsonPropertyName("success")]
    public List<int> Success { get; set; } = [];

    [JsonPropertyName("maintain")]
    public List<int> Maintain { get; set; } = [];

    [JsonPropertyName("decrease")]
    public List<int> Decrease { get; set; } = [];

    [JsonPropertyName("destroy")]
    public List<int> Destroy { get; set; } = [];
}

public sealed class MiscItem
{
    [JsonPropertyName("type")]
    public byte Type { get; set; }

    [JsonPropertyName("gold")]
    public int Gold { get; set; }

    [JsonPropertyName("bonus_gold")]
    public int BonusGold { get; set; }

    [JsonPropertyName("bonus_exp")]
    public int BonusExp { get; set; }

    [JsonPropertyName("parts")]
    public Dictionary<string, UpgradeTable> Parts { get; set; } = [];
}

public sealed class UpgradeFile
{
    [JsonPropertyName("misc")]
    public Dictionary<uint, MiscItem> Misc { get; set; } = [];

    [JsonPropertyName("resale")]
    public Dictionary<uint, int> Resale { get; set; } = [];
}

public sealed class PayoutRule
{
    [JsonPropertyName("outcome")]
    public List<int> Outcome { get; set; } = [];

    [JsonPropertyName("member")]
    public List<double> Member { get; set; } = [];

    [JsonPropertyName("time_min")]
    public int TimeMin { get; set; }

    [JsonPropertyName("time_max")]
    public int TimeMax { get; set; }

    [JsonPropertyName("difficulty")]
    public List<double> Difficulty { get; set; } = [];

    [JsonPropertyName("balance")]
    public List<double> Balance { get; set; } = [];

    [JsonPropertyName("crown")]
    public List<double> Crown { get; set; } = [];

    [JsonPropertyName("wave")]
    public double Wave { get; set; }

    [JsonPropertyName("star")]
    public double Star { get; set; }

    [JsonPropertyName("jessium")]
    public double Jessium { get; set; }

    [JsonPropertyName("kill")]
    public double Kill { get; set; }

    [JsonPropertyName("assist")]
    public double Assist { get; set; }

    [JsonPropertyName("revive")]
    public double Revive { get; set; }

    [JsonPropertyName("survival")]
    public double Survival { get; set; }

    [JsonPropertyName("attack")]
    public double Attack { get; set; }

    [JsonPropertyName("perfect")]
    public double Perfect { get; set; }

    [JsonPropertyName("immortal")]
    public double Immortal { get; set; }

    [JsonPropertyName("longlife")]
    public double LongLife { get; set; }

    [JsonPropertyName("lastkill")]
    public double LastKill { get; set; }

    [JsonPropertyName("firstkill")]
    public double FirstKill { get; set; }

    [JsonPropertyName("combo")]
    public double Combo { get; set; }

    [JsonPropertyName("item")]
    public double Item { get; set; }

    [JsonPropertyName("charger")]
    public double Charger { get; set; }

    [JsonPropertyName("revenge")]
    public double Revenge { get; set; }
}

public sealed class ModePayout
{
    [JsonPropertyName("exp")]
    public PayoutRule? Exp { get; set; }

    [JsonPropertyName("gold")]
    public PayoutRule? Gold { get; set; }
}

public sealed class BonusRule
{
    [JsonPropertyName("rewards")]
    public List<uint> Rewards { get; set; } = [];

    [JsonPropertyName("prob")]
    public List<int> Prob { get; set; } = [];

    [JsonPropertyName("time_min")]
    public int TimeMin { get; set; }
}

public sealed class ResultFile
{
    [JsonPropertyName("payouts")]
    public Dictionary<byte, ModePayout> Payouts { get; set; } = [];

    [JsonPropertyName("bonus")]
    public Dictionary<ushort, BonusRule> Bonus { get; set; } = [];

    [JsonPropertyName("rewards")]
    public Dictionary<uint, int[]> Rewards { get; set; } = [];

    [JsonPropertyName("level_rewards")]
    public Dictionary<byte, List<uint>> LevelRewards { get; set; } = [];

    [JsonPropertyName("missions")]
    public List<MissionInfo> Missions { get; set; } = [];
}

public sealed class MissionInfo
{
    [JsonPropertyName("id")]
    public ushort Id { get; set; }

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    [JsonPropertyName("class")]
    public int Class { get; set; }

    /// <summary>1 easy, 2 normal, 3 hard - the DIFFICULTY of the rule played; 0 = any.</summary>
    [JsonPropertyName("difficulty")]
    public int Difficulty { get; set; }

    /// <summary>Class of the player to kill, 1-5; 0 = any.</summary>
    [JsonPropertyName("target")]
    public int Target { get; set; }

    /// <summary>How to kill: 2 + the kill cause of the host's statistics (4 bomb, 7 hand bomb, 8 poison); 0 = any.</summary>
    [JsonPropertyName("method")]
    public int Method { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("rewards")]
    public List<uint> Rewards { get; set; } = [];

    [JsonPropertyName("prob")]
    public List<int> Prob { get; set; } = [];
}

public sealed class SingleStage
{
    [JsonPropertyName("id")]
    public ushort Id { get; set; }

    [JsonPropertyName("requires")]
    public ushort Requires { get; set; }

    [JsonPropertyName("map")]
    public ushort MapId { get; set; }

    [JsonPropertyName("first_gold")]
    public int FirstGold { get; set; }

    [JsonPropertyName("first_exp")]
    public int FirstExp { get; set; }

    [JsonPropertyName("first_item")]
    public uint FirstItem { get; set; }

    [JsonPropertyName("repeat_gold")]
    public int RepeatGold { get; set; }

    [JsonPropertyName("repeat_exp")]
    public int RepeatExp { get; set; }
}

public sealed class PackageFile
{
    [JsonPropertyName("packages")]
    public Dictionary<uint, PackageInfo> Packages { get; set; } = [];

    [JsonPropertyName("fixed")]
    public Dictionary<uint, FixedItem> Fixed { get; set; } = [];
}

public sealed class CapsulePart
{
    [JsonPropertyName("index")]
    public List<uint> Index { get; set; } = [];

    [JsonPropertyName("gold")]
    public int Gold { get; set; }

    [JsonPropertyName("cash")]
    public int Cash { get; set; }
}

public sealed class CapsuleItems
{
    [JsonPropertyName("items")]
    public List<uint> Items { get; set; } = [];

    [JsonPropertyName("prob")]
    public List<int> Prob { get; set; } = [];

    [JsonPropertyName("opt_gold")]
    public List<uint> OptGold { get; set; } = [];

    [JsonPropertyName("opt_cash")]
    public List<uint> OptCash { get; set; } = [];
}

public sealed class CapsuleOptions
{
    [JsonPropertyName("values")]
    public List<List<int>> Values { get; set; } = [];

    [JsonPropertyName("prob")]
    public List<List<int>> Prob { get; set; } = [];

    [JsonPropertyName("count")]
    public List<int> Count { get; set; } = [];

    [JsonPropertyName("count_prob")]
    public List<int> CountProb { get; set; } = [];

    [JsonPropertyName("expire")]
    public List<int> Expire { get; set; } = [];

    [JsonPropertyName("expire_prob")]
    public List<int> ExpireProb { get; set; } = [];

    [JsonPropertyName("state")]
    public byte State { get; set; } = 1;
}

public sealed class CapsuleFile
{
    [JsonPropertyName("machines")]
    public Dictionary<uint, List<CapsulePart>> Machines { get; set; } = [];

    [JsonPropertyName("items")]
    public Dictionary<uint, CapsuleItems> Items { get; set; } = [];

    [JsonPropertyName("options")]
    public Dictionary<uint, CapsuleOptions> Options { get; set; } = [];
}
