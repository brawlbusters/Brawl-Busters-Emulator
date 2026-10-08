using System.Text.Json.Serialization;

namespace BrawlBusters.Core.Data;

public enum MatchMode : byte
{
    TeamDeathmatch = 1,

    Survival = 2,

    Tutorial = 3,

    Jessium = 4,

    Mission = 5,

    Szm = 6,

    Zim = 7,

    FreeForAll = 8,

    Bsr = 9,

    Channel5Team = 10,
}

public sealed class MapInfo
{
    [JsonPropertyName("id")]
    public ushort Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mode")]
    public MatchMode Mode { get; set; }

    [JsonPropertyName("default")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("released")]
    public bool IsReleased { get; set; }

    [JsonPropertyName("min")]
    public byte MinPlayers { get; set; } = 1;

    [JsonPropertyName("max")]
    public byte MaxPlayers { get; set; } = 1;

    [JsonPropertyName("random")]
    public List<ushort> RandomMaps { get; set; } = [];

    [JsonPropertyName("channels")]
    public List<ushort> Channels { get; set; } = [];

    [JsonPropertyName("rules")]
    public List<ushort> Rules { get; set; } = [];
}

public sealed class RuleInfo
{
    [JsonPropertyName("id")]
    public ushort Id { get; set; }

    [JsonPropertyName("default")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("time")]
    public int Time { get; set; }

    [JsonPropertyName("rounds")]
    public int Rounds { get; set; } = 1;
}

public sealed class MapFile
{
    [JsonPropertyName("maps")]
    public List<MapInfo> Maps { get; set; } = [];

    [JsonPropertyName("rules")]
    public List<RuleInfo> Rules { get; set; } = [];
}
