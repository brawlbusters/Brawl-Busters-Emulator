using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Configuration;

public sealed class EmulatorSettings
{
    public string PublicAddress { get; set; } = "127.0.0.1";

    public List<int> LobbyPorts { get; set; } = [25100, 25110];

    public int RelayPort { get; set; } = 25120;

    public int ChatPort { get; set; } = 25900;

    /// <summary>
    /// HTTP port of the pages the client shows in its built-in browser (notice panel, store home); 0 = no web server.
    /// The pages are the files web/notice.html and web/shop.html.
    /// </summary>
    public int WebPort { get; set; } = 27180;

    public bool SeparateChannelsByCountry { get; set; }

    public LoggingSettings Logging { get; set; } = new();

    public DatabaseSettings Database { get; set; } = new();

    [JsonIgnore]
    public bool LogPackets => Log.IsEnabled(LogChannel.Packets);

    public List<ChannelSettings> Channels { get; set; } =
    [
        new() { Id = 7201 },
        new() { Id = 7301 },
        new() { Id = 7401 },
        new() { Id = 7501 },
        new() { Id = 7601 },
    ];

    public BotSettings Bots { get; set; } = new();

    public AntiCheatSettings AntiCheat { get; set; } = new();

    public Dictionary<string, string> Staff { get; set; } = [];

    public int ExpiredItemGraceDays { get; set; } = 7;

    public int MaxRoomsPerChannel { get; set; } = 200;

    /// <summary>
    /// The client has the text "You cannot extend the item period before it is expired" (Inventory_NotExpiredItem), so a
    /// running item is not extended. Switch off to allow extending at any time.
    /// </summary>
    public bool ExtendOnlyExpiredItems { get; set; } = true;

    /// <summary>
    /// Shortest time between two store / inventory / capsule requests of one player, in milliseconds. A faster one is
    /// answered with the client's "Connection is unstable, please try later" (Inventory_FastRequest). 0 = no limit.
    /// </summary>
    public int MinItemRequestGapMs { get; set; }

    /// <summary>
    /// Longest chat line the server passes on. 70 is the client's own limit: its chat input field is created with
    /// maxChars 70 (Win_Chat.gfx). A longer line can only come from a modified client and is answered with
    /// "Your message is too long".
    /// </summary>
    public int MaxChatLength { get; set; } = 70;

    /// <summary>
    /// Gem score needed for each of the six gem ranks of ranked play, lowest first, while fewer than
    /// <see cref="LadderGradeMinPlayers"/> players have played ranked. A ranked win is worth 20 points.
    /// </summary>
    public int[] LadderGradePoints { get; set; } = [100, 300, 600, 1000, 1500, 2100];

    /// <summary>
    /// From this many ranked players on, the rank scores follow the population (as the client's description says)
    /// instead of <see cref="LadderGradePoints"/>. 0 keeps the fixed scores for good.
    /// </summary>
    public int LadderGradeMinPlayers { get; set; } = 30;

    /// <summary>
    /// Log a player who already has a character straight into the main screen, without the Intro screen state first.
    /// Set to false to get the old order back (sMode 00 for everybody) if a client has trouble logging in.
    /// </summary>
    public bool SkipIntroScreenForExistingCharacters { get; set; } = true;

    /// <summary>
    /// Put a player who enters the lobby for the first time after logging in into the busiest channel he may enter
    /// that is not full, instead of the one his client asks for.
    /// </summary>
    public bool AutoAssignChannel { get; set; } = true;

    /// <summary>Players per team in a ranked match. The original game always played ranked four against four.</summary>
    public int LadderTeamSize { get; set; } = 4;

    /// <summary>Gem score a player loses for leaving a ranked round; the round then counts for nobody.</summary>
    public int LadderLeavePenalty { get; set; } = 50;

    /// <summary>
    /// The most gem score one ranked round can move. How much of it a player wins or loses depends on the own score
    /// against the other team's average - the original formula is not known, this is the usual rating formula.
    /// </summary>
    public int LadderGemFactor { get; set; } = 32;

    /// <summary>"NEW" tags on the main menu: flag byte 2 of the player record, bit 0 My Locker, bit 1 Single Play, bit 2 Ranked.</summary>
    public bool NewTagMyLocker { get; set; }

    public bool NewTagSinglePlay { get; set; }

    public bool NewTagLadder { get; set; }

    public NewAccountSettings NewAccounts { get; set; } = new();

    [JsonIgnore]
    public IPEndPoint RelayEndPoint => new(IPAddress.Parse(PublicAddress), RelayPort);

    public static string RootDirectory { get; } = FindRootDirectory();
    public static string DataDirectory => Path.Combine(RootDirectory, "data");
    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string ConfigPath
        => Environment.GetEnvironmentVariable("BRAWLBUSTERS_CONFIG") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(RootDirectory, "config", "emulator.json");

    public static EmulatorSettings Load()
    {
        string path = ConfigPath;
        var options = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip };

        if (File.Exists(path))
            return JsonSerializer.Deserialize<EmulatorSettings>(File.ReadAllText(path), options) ?? new EmulatorSettings();

        var defaults = new EmulatorSettings();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, options));
        return defaults;
    }

    private static string FindRootDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BrawlBusters.sln"))) return directory.FullName;
        }
        return AppContext.BaseDirectory;
    }
}

public sealed class ChannelSettings
{
    public ushort Id { get; set; }
    public string Name { get; set; } = "";

    public bool StaffOnly { get; set; }

    /// <summary>
    /// The lobby port this channel lives on (one of <see cref="EmulatorSettings.LobbyPorts"/>), 0 = every port.
    /// A player who enters the channel from another port is moved there with a server change
    /// (sTransServer, a new connection, sUserRestart) - the way the original spread channels over servers.
    /// </summary>
    public int LobbyPort { get; set; }
}

public sealed class LoggingSettings
{
    public string ConsoleLevel { get; set; } = "Info";

    public string FileLevel { get; set; } = "Debug";

    public Dictionary<string, bool> Channels { get; set; } = Enum.GetNames<LogChannel>().ToDictionary(name => name, _ => true);

    public void Apply()
    {
        if (Enum.TryParse(ConsoleLevel, ignoreCase: true, out LogLevel console)) Log.ConsoleMinimumLevel = console;
        if (Enum.TryParse(FileLevel, ignoreCase: true, out LogLevel file)) Log.MinimumLevel = file;
        foreach ((string name, bool enabled) in Channels)
            if (Enum.TryParse(name, ignoreCase: true, out LogChannel channel)) Log.SetEnabled(channel, enabled);
    }
}

public sealed class DatabaseSettings
{
    public const string MariaDb = "MariaDb";
    public const string Json = "Json";

    public string Provider { get; set; } = Json;

    public string JsonDirectory { get; set; } = "";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3306;

    public string Name { get; set; } = "rockserver";

    public string User { get; set; } = "root";

    public string Password { get; set; } = "";

    [JsonIgnore]
    public bool UsesMariaDb => Provider.Equals(MariaDb, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Limits for what a client reports about play the server cannot see (see MatchGuard). A report beyond them is
/// logged on the Match channel as "Not plausible" and cut down or ignored; nobody is banned by it.
/// </summary>
public sealed class AntiCheatSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>A single play stage reported as won sooner than this after it loaded is not counted.</summary>
    public int SinglePlayMinSeconds { get; set; } = 10;

    /// <summary>A single play stage can only be started when the stage it requires has been cleared.</summary>
    public bool SinglePlayRequirePrevious { get; set; } = true;

    /// <summary>Kills (and assists) one player can make per minute of a match; 0 = no limit.</summary>
    public int MaxKillsPerMinute { get; set; } = 10;

    /// <summary>Zombies one player can slay per minute of a match; 0 = no limit.</summary>
    public int MaxSlaysPerMinute { get; set; } = 150;

    public int MaxRevivesPerMinute { get; set; } = 6;

    /// <summary>Added to every per-minute limit, so that a short match is not judged too strictly.</summary>
    public int Allowance { get; set; } = 5;

    /// <summary>A survival wave reported as cleared sooner than this after the one before is ignored.</summary>
    public int MinSecondsPerWave { get; set; } = 5;

    /// <summary>The host's end-of-match statistics (My Stats, daily missions) are only used for a match at least this long.</summary>
    public int MinSecondsForStatistics { get; set; } = 60;

    /// <summary>
    /// The check of the client's game tables (see ClientCheck; needs bin/LightFX.dll of tools/client_check on the
    /// player's side): "off", "log" - a login without a verified client is written to the log - or "require" - it
    /// is refused.
    /// </summary>
    public string ClientCheck { get; set; } = "log";

    /// <summary>
    /// The folder (or one file) with the client archives whose tables are the allowed ones, relative to the emulator
    /// folder: every *.bus in it, and the digests listed in its digests.txt. When it does not exist, the client the
    /// emulator sits in (../Data/xmandb.bus) is the allowed one.
    /// </summary>
    public string ClientDataFile { get; set; } = "data/client";

    /// <summary>More allowed table digests (64 hex digits each), for client versions other than ClientDataFile.</summary>
    public List<string> AllowedClientDigests { get; set; } = [];

    /// <summary>How old the last proof of a client may be at login. The module proves itself every minute.</summary>
    public int ClientCheckMaxAgeSeconds { get; set; } = 180;
}

public sealed class BotSettings
{
    public bool Enabled { get; set; } = true;

    public int Count { get; set; } = 30;

    public bool JoinPlayerRooms { get; set; } = true;

    public bool ShowOnLeaderboard { get; set; }

    public bool OpenRooms { get; set; }

    public bool FillChannels { get; set; } = true;

    public int ChannelCapacity { get; set; } = 200;

    public int FillSeconds { get; set; } = 180;
}

public sealed class NewAccountSettings
{
    public byte Level { get; set; } = 30;

    public int Gold { get; set; } = 999_999_999;

    public int Cash { get; set; } = 999_999_999;

    public bool ApplyToExisting { get; set; } = true;

    public string TestPassword { get; set; } = "pass1234";

    public bool PasswordNeedsPunctuation { get; set; }

    public bool SeedTestAccount { get; set; } = true;

    public string SeedLoginId { get; set; } = "test";

    public string SeedPassword { get; set; } = "test";
}
