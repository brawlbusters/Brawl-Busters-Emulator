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
