using System.Text.Json;
using System.Text.Json.Serialization;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Data;

public sealed class GameData
{
    private static readonly Lazy<GameData> Lazy = new(Load);

    private readonly int[] _levelExp;

    private readonly List<MapInfo> _mapList;

    private GameData(Dictionary<uint, CatalogEntry> catalog, Dictionary<uint, ItemInfo> items, int[] levelExp, MapFile maps,
        PackageFile packages, CapsuleFile capsules, List<SingleStage> stages, UpgradeFile upgrades, ResultFile results)
    {
        Results = results;
        Upgrades = upgrades;
        SingleStages = stages.ToDictionary(stage => stage.Id);
        Catalog = catalog;
        Items = items;
        Packages = packages;
        Capsules = capsules;
        _levelExp = levelExp;
        _mapList = maps.Maps;
        Maps = maps.Maps.ToDictionary(map => map.Id);
        Rules = maps.Rules.ToDictionary(rule => rule.Id);
        Channels = maps.Channels.ToDictionary(channel => channel.Id);
        CensoredWords = maps.Censored;
    }

    public IReadOnlyList<string> CensoredWords { get; }

    /// <summary>False when the item asks for a higher level than <paramref name="level"/> ("Requires Level N or higher").</summary>
    public bool LevelAllows(uint itemId, byte level) => !Items.TryGetValue(itemId, out ItemInfo info) || info.MinLevel <= level;

    public IReadOnlyDictionary<ushort, ChannelData> Channels { get; }

    public byte ChannelType(ushort channelId) => Channels.TryGetValue(channelId, out ChannelData? channel) ? channel.Type : (byte)channelId;

    public IReadOnlyDictionary<ushort, MapInfo> Maps { get; }

    public IReadOnlyDictionary<ushort, RuleInfo> Rules { get; }

    public IEnumerable<MapInfo> MapsOf(MatchMode mode) => _mapList.Where(map => map.Mode == mode && map.IsReleased);

    public MapInfo? DefaultMap(MatchMode mode, byte maxPlayers)
    {
        List<MapInfo> maps = MapsOf(mode).ToList();
        return maps.FirstOrDefault(map => map.IsDefault && map.MaxPlayers >= maxPlayers)
            ?? maps.FirstOrDefault(map => map.IsDefault)
            ?? maps.FirstOrDefault();
    }

    public ushort DefaultRule(MapInfo map)
    {
        foreach (ushort id in map.Rules)
            if (Rules.TryGetValue(id, out RuleInfo? rule) && rule.IsDefault) return id;
        return map.Rules.Count > 0 ? map.Rules[0] : (ushort)0;
    }

    public byte MaxLevel => (byte)Math.Max(1, _levelExp.Length);

    public int ExpForLevel(int level)
        => _levelExp.Length == 0 ? 0 : _levelExp[Math.Clamp(level, 1, _levelExp.Length) - 1];

    public byte LevelForExp(int exp, byte current)
    {
        int level = 1;
        while (level < _levelExp.Length && exp >= _levelExp[level]) level++;
        return (byte)Math.Max(level, current);
    }

    public static GameData Instance => Lazy.Value;

    public IReadOnlyDictionary<uint, CatalogEntry> Catalog { get; }

    public IReadOnlyDictionary<uint, ItemInfo> Items { get; }

    public byte TypeOf(uint itemId) => Items.TryGetValue(itemId, out ItemInfo info) ? info.Type : (byte)0;

    public PackageFile Packages { get; }

    public ResultFile Results { get; }

    public UpgradeFile Upgrades { get; }

    public static string? UpgradePart(byte itemType) => itemType switch
    {
        ItemType.Weapon => "WEAPON",
        ItemType.Helmet => "HELMET",
        ItemType.Upper => "UPPER",
        ItemType.Hand => "HAND",
        ItemType.Lower => "LOWER",
        ItemType.Foot => "FOOT",
        ItemType.UpperAlt => "SUIT",
        _ => null,
    };

    public IReadOnlyDictionary<ushort, SingleStage> SingleStages { get; }

    public CapsuleFile Capsules { get; }

    private static GameData Load()
    {
        string directory = Path.Combine(EmulatorSettings.DataDirectory, "game");
        var catalog = Read<Dictionary<uint, CatalogEntry>>(Path.Combine(directory, "catalog.json"));
        var items = Read<Dictionary<uint, int[]>>(Path.Combine(directory, "items.json"))
            .ToDictionary(pair => pair.Key, pair => ItemInfo.FromArray(pair.Value));
        var packages = Read<PackageFile>(Path.Combine(directory, "packages.json"));
        var capsules = Read<CapsuleFile>(Path.Combine(directory, "capsules.json"));

        if (catalog.Count == 0)
            Log.Warn("GameData", $"No shop catalog in {directory} - run tools/export_gamedata.py. The shop will refuse purchases.");
        else
            Log.Info("GameData", $"Loaded {catalog.Count} catalog entries and {items.Count} items.");

        int[] levels = Read<List<int>>(Path.Combine(directory, "levels.json")).ToArray();
        var maps = Read<MapFile>(Path.Combine(directory, "maps.json"));
        if (maps.Maps.Count == 0)
            Log.Warn("GameData", $"No maps in {directory} - run tools/export_gamedata.py. Rooms will use the recorded survival map only.");
        else
            Log.Info("GameData", $"Loaded {maps.Maps.Count} maps and {maps.Rules.Count} rule sets.");
        Log.Info("GameData", $"Loaded {packages.Packages.Count} packages and {capsules.Machines.Count} capsule machines.");
        var stages = Read<List<SingleStage>>(Path.Combine(directory, "singleplay.json"));
        var upgrades = Read<UpgradeFile>(Path.Combine(directory, "upgrades.json"));
        var results = Read<ResultFile>(Path.Combine(directory, "results.json"));
        return new GameData(catalog, items, levels, maps, packages, capsules, stages, upgrades, results);
    }

    private static T Read<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
    }
}

public sealed class CatalogEntry
{
    [JsonPropertyName("gold")]
    public List<int> Gold { get; set; } = [];

    [JsonPropertyName("cash")]
    public List<int> Cash { get; set; } = [];

    [JsonPropertyName("count")]
    public List<int> Count { get; set; } = [];

    [JsonPropertyName("expire")]
    public List<int> Expire { get; set; } = [];

    [JsonPropertyName("extend")]
    public List<int> Extend { get; set; } = [];

    [JsonPropertyName("extend_gold")]
    public List<int> ExtendGold { get; set; } = [];

    [JsonPropertyName("extend_cash")]
    public List<int> ExtendCash { get; set; } = [];

    /// <summary>RT price, per reinforce level, of keeping the item from going down a level / from breaking.</summary>
    [JsonPropertyName("insure_decrease")]
    public List<int> InsureDecrease { get; set; } = [];

    [JsonPropertyName("insure_destroy")]
    public List<int> InsureDestroy { get; set; } = [];

    [JsonPropertyName("options")]
    public List<List<int>> Options { get; set; } = [];
}
