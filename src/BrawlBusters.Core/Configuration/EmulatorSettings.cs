using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrawlBusters.Core.Configuration;

public sealed class EmulatorSettings
{
    public string PublicAddress { get; set; } = "127.0.0.1";

    public List<int> AuthPorts { get; set; } = [25100, 25110];

    public int MainPort { get; set; } = 25120;

    public int ChatPort { get; set; } = 25900;

    public int CastPort { get; set; } = 25200;

    public bool LogPackets { get; set; } = true;

    public string ConsoleLogLevel { get; set; } = "Info";

    public List<ChannelSettings> Channels { get; set; } =
    [
        new() { Id = 1, Name = "Channel 1" },
        new() { Id = 2, Name = "Channel 2" },
    ];

    public BotSettings Bots { get; set; } = new();

    [JsonIgnore]
    public IPEndPoint MainEndPoint => new(IPAddress.Parse(PublicAddress), MainPort);
    [JsonIgnore]
    public IPEndPoint CastEndPoint => new(IPAddress.Parse(PublicAddress), CastPort);

    public static string RootDirectory { get; } = FindRootDirectory();
    public static string DataDirectory => Path.Combine(RootDirectory, "data");
    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static EmulatorSettings Load()
    {
        string path = Path.Combine(RootDirectory, "config", "emulator.json");
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
}

public sealed class BotSettings
{
    public bool Enabled { get; set; } = true;

    public int Count { get; set; } = 30;

    public bool JoinPlayerRooms { get; set; } = true;
}
