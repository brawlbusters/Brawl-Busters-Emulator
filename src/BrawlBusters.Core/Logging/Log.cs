using System.Collections.Concurrent;

namespace BrawlBusters.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>A subject area of the log. Each one can be switched off in the config or at runtime (<c>log</c> command).</summary>
public enum LogChannel
{
    General,
    Network,
    Packets,
    Session,
    Lobby,
    Room,
    Match,
    Chat,
    Bots,
    Database,
    Commands,
}

public static class Log
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<LogChannel, bool> Muted = new();
    private static StreamWriter? _file;
    private static StreamWriter? _missing;

    private static bool IsMissingPacket(string message)
        => message.Contains("(not implemented)", StringComparison.Ordinal)
           || message.StartsWith("No handler for", StringComparison.Ordinal)
           || message.StartsWith("Malformed", StringComparison.Ordinal)
           || message.Contains("not handled", StringComparison.Ordinal);

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public static LogLevel ConsoleMinimumLevel { get; set; } = LogLevel.Info;

    public static bool IsEnabled(LogChannel channel) => !Muted.ContainsKey(channel);

    public static void SetEnabled(LogChannel channel, bool enabled)
    {
        if (enabled) Muted.TryRemove(channel, out _);
        else Muted[channel] = true;
    }

    public static IEnumerable<(LogChannel Channel, bool Enabled)> Channels()
        => Enum.GetValues<LogChannel>().Select(channel => (channel, IsEnabled(channel)));

    public static void ToFile(string directory, string serverName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{serverName}-{DateTime.Now:yyyyMMdd}.log");
        lock (Gate)
        {
            _file?.Dispose();
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            _missing?.Dispose();
            _missing = new StreamWriter(
                new FileStream(Path.Combine(directory, $"missing-packets-{serverName}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
    }

    public static void Debug(string scope, string message) => Write(LogLevel.Debug, LogChannel.General, scope, message);
    public static void Info(string scope, string message) => Write(LogLevel.Info, LogChannel.General, scope, message);
    public static void Warn(string scope, string message) => Write(LogLevel.Warn, LogChannel.General, scope, message);
    public static void Error(string scope, string message) => Write(LogLevel.Error, LogChannel.General, scope, message);
    public static void Error(string scope, Exception exception) => Write(LogLevel.Error, LogChannel.General, scope, exception.ToString());

    public static void Debug(LogChannel channel, string scope, string message) => Write(LogLevel.Debug, channel, scope, message);
    public static void Info(LogChannel channel, string scope, string message) => Write(LogLevel.Info, channel, scope, message);
    public static void Warn(LogChannel channel, string scope, string message) => Write(LogLevel.Warn, channel, scope, message);
    public static void Error(LogChannel channel, string scope, string message) => Write(LogLevel.Error, channel, scope, message);

    public static string Hex(ReadOnlySpan<byte> data, int maxBytes = 256)
    {
        string hex = Convert.ToHexString(data[..Math.Min(data.Length, maxBytes)]);
        return data.Length > maxBytes ? $"{hex}... ({data.Length} bytes)" : hex;
    }

    private static void Write(LogLevel level, LogChannel channel, string scope, string message)
    {
        if (level < MinimumLevel) return;
        if (level < LogLevel.Warn && !IsEnabled(channel)) return;

        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] [{scope}] {message}";
        lock (Gate)
        {
            if (level >= ConsoleMinimumLevel)
            {
                Console.ForegroundColor = level switch
                {
                    LogLevel.Debug => ConsoleColor.DarkGray,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    _ => ConsoleColor.Gray,
                };
                Console.WriteLine(line);
                Console.ResetColor();
            }
            _file?.WriteLine(line);
            if (level == LogLevel.Warn && _missing is not null && IsMissingPacket(message))
                _missing.WriteLine(line);
        }
    }
}
