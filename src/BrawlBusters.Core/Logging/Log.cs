namespace BrawlBusters.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public static LogLevel ConsoleMinimumLevel { get; set; } = LogLevel.Info;

    public static void ToFile(string directory, string serverName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{serverName}-{DateTime.Now:yyyyMMdd}.log");
        lock (Gate)
        {
            _file?.Dispose();
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
    }

    public static void Debug(string scope, string message) => Write(LogLevel.Debug, scope, message);
    public static void Info(string scope, string message) => Write(LogLevel.Info, scope, message);
    public static void Warn(string scope, string message) => Write(LogLevel.Warn, scope, message);
    public static void Error(string scope, string message) => Write(LogLevel.Error, scope, message);
    public static void Error(string scope, Exception exception) => Write(LogLevel.Error, scope, exception.ToString());

    public static string Hex(ReadOnlySpan<byte> data, int maxBytes = 256)
    {
        string hex = Convert.ToHexString(data[..Math.Min(data.Length, maxBytes)]);
        return data.Length > maxBytes ? $"{hex}... ({data.Length} bytes)" : hex;
    }

    private static void Write(LogLevel level, string scope, string message)
    {
        if (level < MinimumLevel) return;

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
        }
    }
}
