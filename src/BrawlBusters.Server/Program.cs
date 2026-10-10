using System.Net;
using BrawlBusters.Core.Chat;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Persistence;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;
using BrawlBusters.Core.Sessions.Handlers;
using BrawlBusters.Server;

const string ServerName = "Server";

Console.Title = "Brawl Busters - Server";
Log.ToFile(EmulatorSettings.LogDirectory, ServerName);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    EmulatorSettings settings = EmulatorSettings.Load();
    settings.Logging.Apply();
    //Log.Info(ServerName, $"Root: {EmulatorSettings.RootDirectory} (config {EmulatorSettings.ConfigPath})");

    IAccountBackend backend;
    IReadOnlyList<ChannelSettings> channels = settings.Channels;
    if (settings.Database.UsesMariaDb)
    {
        var database = new MariaDbAccountBackend(settings.Database);
        await database.EnsureSchemaAsync(Path.Combine(EmulatorSettings.RootDirectory, "database", "schema.sql"), shutdown.Token);
        channels = await database.LoadChannelsAsync(settings.Channels, shutdown.Token);
        await ImportJsonAccountsAsync(database, shutdown.Token);
        backend = database;
    }
    else
    {
        backend = new JsonAccountBackend(settings.Database.JsonDirectory.Length > 0 ? settings.Database.JsonDirectory : EmulatorSettings.DataDirectory);
    }

    await using AccountRepository accounts = await AccountRepository.OpenAsync(backend, shutdown.Token);
    ChannelDirectory.Configure(channels);
    BotDirector.ChannelCapacity = settings.Bots.ChannelCapacity;
    AuthorityNotifier.Attach(accounts);
    Log.Info(ServerName, $"Channels: {string.Join(", ", channels.Select(channel => channel.Id + (channel.StaffOnly ? " (staff)" : "")))}");

    NewAccountSettings start = settings.NewAccounts;
    if (start.SeedTestAccount && accounts.FindByLoginId(start.SeedLoginId) is null
        && accounts.Create(start.SeedLoginId, PasswordHasher.Hash(start.SeedPassword)) is { } seeded)
    {
        accounts.Update(seeded.Id, account =>
        {
            account.Level = start.Level;
            account.Experience = GameData.Instance.ExpForLevel(start.Level);
            account.Gold = start.Gold;
            account.Cash = start.Cash;
        });
        Log.Info(ServerName, $"Created the test account '{start.SeedLoginId}' (uid {seeded.Id}) - switch NewAccounts.SeedTestAccount off on a public server");
    }
    if (start.ApplyToExisting)
    {
        int raised = accounts.RaiseAll(start.Level, GameData.Instance.ExpForLevel(start.Level), start.Gold, start.Cash);
        if (raised > 0) Log.Info(ServerName, $"Raised {raised} account(s) to at least level {start.Level}, {start.Gold:N0} BP and {start.Cash:N0} RT");
    }

    MessageRouter router = StandardRouter.Create();
    var chatHub = new ChatHub(accounts);
    var tasks = new List<Task>();

    foreach (int port in settings.LobbyPorts.Append(settings.RelayPort).Distinct())
    {
        var endPoint = new IPEndPoint(IPAddress.Any, port);
        tasks.Add(new GameServer("Lobby", endPoint, (connection, token) => new LobbySession(connection, settings, accounts, router).RunAsync(token)).RunAsync(shutdown.Token));
        tasks.Add(new HolePunchServer("Udp", endPoint).RunAsync(shutdown.Token));
    }

    tasks.Add(new GameServer("Chat", new IPEndPoint(IPAddress.Any, settings.ChatPort),
        (connection, token) => new ChatSession(connection, settings, chatHub).RunAsync(token)).RunAsync(shutdown.Token));
    if (settings.WebPort > 0)
        tasks.Add(new WebServer(new IPEndPoint(IPAddress.Any, settings.WebPort), Path.Combine(EmulatorSettings.RootDirectory, "web")).RunAsync(shutdown.Token));
    tasks.Add(BotDirector.RunAsync("Bots", settings, shutdown.Token));
    tasks.Add(LobbyFeed.RunAsync(shutdown.Token));
    ChatHub.MaxChatLength = settings.MaxChatLength;
    tasks.Add(ServerConsole.RunAsync(accounts, shutdown));

    await Task.WhenAll(tasks);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Log.Error(ServerName, exception);
    return 1;
}

static async Task ImportJsonAccountsAsync(MariaDbAccountBackend database, CancellationToken cancellationToken)
{
    string file = Path.Combine(EmulatorSettings.DataDirectory, "accounts.json");
    if ((await database.LoadAllAsync(cancellationToken)).Count > 0) return;
    if (!File.Exists(file))
    {
        Log.Info(LogChannel.Database, "Database", "The database has no accounts yet and there is no accounts.json to import - starting empty");
        return;
    }

    List<Account> legacy = await new JsonAccountBackend(EmulatorSettings.DataDirectory).LoadAllAsync(cancellationToken);
    int imported = 0;
    foreach (Account account in legacy)
    {
        try
        {
            await database.SaveAsync([account], cancellationToken);
            imported++;
        }
        catch (Exception exception)
        {
            Log.Warn(LogChannel.Database, "Database", $"Account '{account.LoginId}' (uid {account.Id}, nick '{account.Nickname}') was not imported: {exception.Message}");
        }
    }
    Log.Info(LogChannel.Database, "Database", $"First start on an empty database: imported {imported} of {legacy.Count} account(s) from {file}");
}
