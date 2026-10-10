using BrawlBusters.Core.Data;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

/// <summary>
/// Base of the slash commands. A command is one class in the Commands folder that derives from this one; it is
/// found and registered by itself (<see cref="CommandRegistry"/>), there is no list to add it to.
/// </summary>
public abstract class ChatCommand : IChatCommand
{
    public abstract string Name { get; }

    public virtual string[] Aliases => [];

    public abstract Permission Required { get; }

    public abstract string Usage { get; }

    public abstract string Description { get; }

    public abstract Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken);

    protected static string Need(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new CommandUsageException();

    protected static long NeedNumber(string[] arguments, int index)
        => long.TryParse(Need(arguments, index), out long value) ? value : throw new CommandUsageException();

    protected static string? Optional(string[] arguments, int index) => index < arguments.Length ? arguments[index] : null;

    protected static int AddCapped(int current, long amount) => (int)Math.Clamp(current + amount, 0, int.MaxValue);

    /// <summary>Pushes the changed numbers of <paramref name="userId"/> to the game client if that player is online.</summary>
    protected static async Task RefreshClientAsync(uint userId, CancellationToken cancellationToken)
    {
        if (SessionRegistry.Find(userId) is not { } session) return;

        byte levelBefore = session.Account.DisplayLevel;
        session.RefreshAccount();
        Account account = session.Account;
        await session.SendAsync(UserInfoPacket.ExpAndGold((uint)account.Experience, (uint)account.Gold), cancellationToken);
        await session.SendAsync(UserInfoPacket.Cash((uint)account.Cash), cancellationToken);
        if (account.DisplayLevel != levelBefore) await session.SendAsync(UserInfoPacket.Level(account.DisplayLevel), cancellationToken);
    }
}
