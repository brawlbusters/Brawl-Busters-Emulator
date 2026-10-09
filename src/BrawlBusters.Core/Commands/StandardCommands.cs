using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public static class StandardCommands
{
    public static CommandRegistry Create() => new CommandRegistry()
        .Add(new HelpCommand())
        .Add(new OnlineCommand())
        .Add(new ObserveCommand())
        .Add(new KickCommand())
        .Add(new NoticeCommand())
        .Add(new BanCommand())
        .Add(new UnbanCommand())
        .Add(new CurrencyCommand("gold", "BP", (account, amount) => account.Gold = AddCapped(account.Gold, amount)))
        .Add(new CurrencyCommand("cash", "RT", (account, amount) => account.Cash = AddCapped(account.Cash, amount)))
        .Add(new CurrencyCommand("exp", "exp", (account, amount) =>
        {
            account.Experience = AddCapped(account.Experience, amount);
            account.Level = GameData.Instance.LevelForExp(account.Experience, account.Level);
        }))
        .Add(new LevelCommand())
        .Add(new ItemCommand())
        .Add(new GradeCommand())
        .Add(new LogCommand());

    private static int AddCapped(int current, long amount) => (int)Math.Clamp(current + amount, 0, int.MaxValue);

    /// <summary>Pushes the changed numbers of <paramref name="userId"/> to the game client if that player is online.</summary>
    public static async Task RefreshClientAsync(uint userId, CancellationToken cancellationToken)
    {
        if (SessionRegistry.Find(userId) is not { } session) return;

        byte levelBefore = session.Account.DisplayLevel;
        session.RefreshAccount();
        Account account = session.Account;
        await session.SendAsync(UserInfoPacket.ExpAndGold((uint)account.Experience, (uint)account.Gold), cancellationToken);
        await session.SendAsync(UserInfoPacket.Cash((uint)account.Cash), cancellationToken);
        if (account.DisplayLevel != levelBefore) await session.SendAsync(UserInfoPacket.Level(account.DisplayLevel), cancellationToken);
    }

    private static string Need(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new CommandUsageException();

    private static long NeedNumber(string[] arguments, int index)
        => long.TryParse(Need(arguments, index), out long value) ? value : throw new CommandUsageException();

    private static string? Optional(string[] arguments, int index) => index < arguments.Length ? arguments[index] : null;

    private sealed class HelpCommand : IChatCommand
    {
        public string Name => "help";
        public string[] Aliases => ["commands", "?"];
        public Permission Required => Permission.None;
        public string Usage => "help";
        public string Description => "lists the commands you may use";

        public async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            List<IChatCommand> allowed = CommandRegistry.Instance.Commands
                .Where(command => command is not HelpCommand && context.Can(command.Required))
                .ToList();
            string grade = context.Caller is { } caller ? AuthorityNotifier.NameOf(caller.Grade) : "Console";
            if (allowed.Count == 0)
            {
                await context.Reply($"[System] Your grade is {grade}. There are no commands for this grade.");
                return;
            }

            await context.Reply($"[System] Your grade is {grade}. Commands you may use:");
            foreach (IChatCommand command in allowed)
                await context.Reply($"{CommandRegistry.Prefix}{command.Usage} - {command.Description}");
        }
    }

    private sealed class OnlineCommand : IChatCommand
    {
        public string Name => "online";
        public string[] Aliases => ["who"];
        public Permission Required => Permission.ListPlayers;
        public string Usage => "online";
        public string Description => "shows who is connected and where";

        public async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            List<ClientSession> sessions = SessionRegistry.All();
            await context.Reply($"{sessions.Count} real player(s) connected");
            foreach (ChannelSettings channel in ChannelDirectory.Channels)
            {
                int real = SessionRegistry.InChannel(channel.Id);
                int simulated = BotDirector.InLobby(channel.Id);
                string name = GameData.Instance.Channels.TryGetValue(channel.Id, out ChannelData? data) ? data.Text : channel.Name;
                await context.Reply($"Channel {channel.Id} ({name}): {real} real player(s) in the lobby"
                    + (simulated > 0 ? $", {simulated} simulated (bot fill, not connections)" : ""));
            }
            foreach (ClientSession session in sessions.OrderBy(session => session.Account.Nickname))
            {
                string place = session.Room is { } room
                    ? $"room {room.Id} ({room.Title}){(session.InMatch ? ", in a match" : "")}"
                    : session.ChannelId != 0 ? $"lobby of channel {session.ChannelId}" : "menus";
                await context.Reply($"{session.Account.Nickname} [{session.Account.Grade}, level {session.Account.DisplayLevel}] - {place}");
            }
        }
    }

    private sealed class ObserveCommand : IChatCommand
    {
        public string Name => "observe";
        public string[] Aliases => ["gmo", "gm_observe"];
        public Permission Required => Permission.ObserveMatches;
        public string Usage => "observe <room number>";
        public string Description => "enters a room as an observer";

        public Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            if (!ushort.TryParse(Need(arguments, 0), out ushort roomId)) throw new CommandUsageException();
            return context.Session is { } session
                ? GameFlow.GmObserveAsync(session, roomId, cancellationToken)
                : context.Reply("Only a logged-in player can observe a room.");
        }
    }

    private sealed class KickCommand : IChatCommand
    {
        public string Name => "kick";
        public Permission Required => Permission.Kick;
        public string Usage => "kick <nickname>";
        public string Description => "disconnects a player";

        public Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            Account? target = context.Target(Need(arguments, 0));
            if (target is null || SessionRegistry.Find(target.Id) is not { } session) return context.Reply("That player is not online.");
            if (context.Caller is { } caller && target.Grade > caller.Grade) return context.Reply("You cannot kick a higher grade.");

            session.Connection.Close();
            return context.Reply($"{target.Nickname} was disconnected.");
        }
    }

    private sealed class NoticeCommand : IChatCommand
    {
        public string Name => "notice";
        public string[] Aliases => ["gm"];
        public Permission Required => Permission.Notice;
        public string Usage => "notice <text>";
        public string Description => "shows a system message to every player";

        public async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            if (arguments.Length == 0) throw new CommandUsageException();
            string text = string.Join(' ', arguments);
            int delivered = await SessionRegistry.BroadcastAsync(() => UserMsgPacket.SystemMessage(text), cancellationToken);
            await context.Reply($"Notice sent to {delivered} player(s).");
        }
    }

    private sealed class BanCommand : IChatCommand
    {
        public string Name => "ban";
        public Permission Required => Permission.Ban;
        public string Usage => "ban <nickname> <minutes>";
        public string Description => "locks an account for a while and disconnects it";

        public Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            Account? target = context.Target(Need(arguments, 0));
            long minutes = NeedNumber(arguments, 1);
            if (target is null || minutes <= 0) return context.Reply("No such player.");
            if (context.Caller is { } caller && target.Grade >= caller.Grade) return context.Reply("You cannot ban that grade.");

            DateTime until = DateTime.UtcNow.AddMinutes(minutes);
            context.Accounts.Update(target.Id, account => account.BannedUntilUtc = until);
            return context.Reply($"{target.Nickname} is banned until {until:yyyy-MM-dd HH:mm} UTC.");
        }
    }

    private sealed class UnbanCommand : IChatCommand
    {
        public string Name => "unban";
        public Permission Required => Permission.Ban;
        public string Usage => "unban <nickname>";
        public string Description => "lifts a ban";

        public Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            Account? target = context.Target(Need(arguments, 0));
            if (target is null) return context.Reply("No such player.");

            context.Accounts.Update(target.Id, account => account.BannedUntilUtc = null);
            return context.Reply($"{target.Nickname} may log in again.");
        }
    }

    private sealed class CurrencyCommand(string name, string unit, Action<Account, long> apply) : IChatCommand
    {
        public string Name => name;
        public Permission Required => Permission.GiveCurrency;
        public string Usage => $"{name} <amount> [nickname]";
        public string Description => $"adds {unit} (a negative amount takes it away)";

        public async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            long amount = NeedNumber(arguments, 0);
            Account? target = context.Target(Optional(arguments, 1));
            if (target is null)
            {
                await context.Reply("No such player.");
                return;
            }

            context.Accounts.Update(target.Id, account => apply(account, amount));
            await RefreshClientAsync(target.Id, cancellationToken);
            await context.Reply($"{target.Nickname}: {amount:+#;-#;0} {unit}.");
        }
    }

    private sealed class LevelCommand : IChatCommand
    {
        public string Name => "level";
        public Permission Required => Permission.GiveCurrency;
        public string Usage => "level <1-99> [nickname]";
        public string Description => "sets a player's level";

        public async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            long level = NeedNumber(arguments, 0);
            Account? target = context.Target(Optional(arguments, 1));
            if (target is null || level is < 1 or > 99)
            {
                await context.Reply("No such player, or the level is out of range.");
                return;
            }

            context.Accounts.Update(target.Id, account =>
            {
                account.Level = (byte)level;
                account.Experience = GameData.Instance.ExpForLevel((byte)level);
            });
            await RefreshClientAsync(target.Id, cancellationToken);
            await context.Reply($"{target.Nickname} is now level {level}.");
        }
    }

    private sealed class ItemCommand : IChatCommand
    {
        public string Name => "item";
        public Permission Required => Permission.GiveItems;
        public string Usage => "item <item id> [quantity] [nickname]";
        public string Description => "puts an item into a player's inventory";

        public async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            long itemId = NeedNumber(arguments, 0);
            long quantity = arguments.Length > 1 ? NeedNumber(arguments, 1) : 1;
            Account? target = context.Target(Optional(arguments, 2));
            GameData data = GameData.Instance;
            if (target is null || itemId <= 0 || quantity is < 1 or > ushort.MaxValue || !data.Items.ContainsKey((uint)itemId))
            {
                await context.Reply("No such player or item.");
                return;
            }

            InventoryItem? granted = null;
            context.Accounts.Update(target.Id, account =>
            {
                granted = new InventoryItem
                {
                    Slot = account.FreeSlot(),
                    ItemId = (uint)itemId,
                    Type = data.TypeOf((uint)itemId),
                    Quantity = (ushort)quantity,
                };
                account.Items.Add(granted);
            });

            if (granted is not null && SessionRegistry.Find(target.Id) is { } session)
            {
                session.RefreshAccount();
                await session.SendAsync(InventoryPacket.Added([granted]), cancellationToken);
            }
            await context.Reply($"{target.Nickname} received item {itemId} x{quantity}.");
        }
    }

    private sealed class GradeCommand : IChatCommand
    {
        public string Name => "grade";
        public Permission Required => Permission.SetGrade;
        public string Usage => "grade <nickname> <player|mod|gm|dev>";
        public string Description => "changes an account's grade; the player is told and gets the rights at once";

        public Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            Account? target = context.Target(Need(arguments, 0));
            if (target is null || !Permissions.TryParseGrade(Need(arguments, 1), out AccountGrade grade)) throw new CommandUsageException();

            context.Accounts.Update(target.Id, account => account.Grade = grade);
            return context.Reply($"{target.Nickname} is now {AuthorityNotifier.NameOf(grade)}.");
        }
    }

    private sealed class LogCommand : IChatCommand
    {
        public string Name => "log";
        public Permission Required => Permission.ManageServer;
        public string Usage => "log [channel] [on|off]";
        public string Description => "shows or switches the log channels (packets, bots, database, ...)";

        public Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            if (arguments.Length == 0)
                return context.Reply(string.Join(", ", Log.Channels().Select(entry => $"{entry.Channel} {(entry.Enabled ? "on" : "off")}")));

            if (!Enum.TryParse(arguments[0], ignoreCase: true, out LogChannel channel)) throw new CommandUsageException();
            bool enabled = arguments.Length > 1 ? arguments[1].Equals("on", StringComparison.OrdinalIgnoreCase) : !Log.IsEnabled(channel);
            Log.SetEnabled(channel, enabled);
            return context.Reply($"Log channel {channel} is {(enabled ? "on" : "off")}.");
        }
    }
}
