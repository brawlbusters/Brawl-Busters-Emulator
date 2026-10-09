using System.Security.Cryptography;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Sessions;

public abstract class ClientSession
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly MessageRouter _router;
    private int _wrongPasswords;

    protected ClientSession(GameConnection connection, EmulatorSettings settings, AccountRepository accounts, MessageRouter router)
    {
        Connection = connection;
        Settings = settings;
        Accounts = accounts;
        _router = router;
    }

    public GameConnection Connection { get; }
    public EmulatorSettings Settings { get; }
    public AccountRepository Accounts { get; }

    public Account Account { get; private set; } = null!;

    public ushort ChannelId { get; set; }

    public Room? Room { get; set; }

    /// <summary>The lobby player count this client was last told (see <see cref="LobbyFeed"/>).</summary>
    public ushort LobbyPlayers { get; set; }

    /// <summary>The ping to this server the client last reported (cUserInfo 04), in milliseconds.</summary>
    public ushort ServerPing { get; set; }

    private long _lastItemRequestTick;

    /// <summary>True when this store / inventory request comes sooner after the last one than the settings allow.</summary>
    public bool IsItemRequestTooFast()
    {
        int gap = Settings.MinItemRequestGapMs;
        long now = Environment.TickCount64;
        bool tooFast = gap > 0 && now - _lastItemRequestTick < gap;
        if (!tooFast) _lastItemRequestTick = now;
        return tooFast;
    }

    /// <summary>Set when this connection is the second half of a server change: the channel the player was on his way to.</summary>
    public ushort? ServerChangeChannel { get; private set; }

    /// <summary>The lobby port this client is connected to.</summary>
    public int LocalPort => Connection.LocalPort;

    /// <summary>The room list this client was last told about; null until it has asked for one (see <see cref="LobbyFeed"/>).</summary>
    public Dictionary<ushort, Protocol.Packets.RoomListEntry>? LobbyRooms { get; set; }

    public bool InMatch { get; set; }

    public ushort SingleStage { get; set; }

    public bool SingleStageFinished { get; set; }

    public string Tag => Account is null ? Connection.Tag : $"{Connection.Tag} {Account.LoginId}";

    protected abstract bool AcceptsLogin { get; }

    protected abstract bool AcceptsTransfer { get; }

    protected abstract Task OnSessionStartedAsync(CancellationToken cancellationToken);

    protected virtual Task OnSessionEndedAsync() => Task.CompletedTask;

    public Task SendAsync(PacketWriter message, CancellationToken cancellationToken = default)
    {
        if (Log.IsEnabled(LogChannel.Packets))
        {
            byte[] bytes = message.ToArray();
            Log.Debug(LogChannel.Packets, Tag, $"SEND {(MsgCategory)bytes[0],-16} {Log.Hex(bytes.AsSpan(1))}");
        }
        return Connection.SendAsync(message, cancellationToken);
    }

    public void RefreshAccount() => Account = Accounts.FindById(Account.Id) ?? Account;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        uint seed = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        await Connection.SendRawAsync(ServerGreeting.Build(ProtocolConstants.LobbyGreeting, seed), cancellationToken);

        if (!await HandshakeAsync(seed, cancellationToken)) return;

        using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task keepAlive = Task.CompletedTask;
        try
        {
            await World.RunAsync(async () =>
            {
                SessionRegistry.Replace(this);
                await OnSessionStartedAsync(cancellationToken);
            });
            keepAlive = SendKeepAlivesAsync(sessionLifetime.Token);

            while (await Connection.ReceiveAsync(cancellationToken) is { } message)
                await _router.DispatchAsync(this, message, cancellationToken);
        }
        finally
        {
            sessionLifetime.Cancel();
            await keepAlive;
            await World.RunAsync(async () =>
            {
                SessionRegistry.Remove(this);
                GameFlow.Disconnected(this);
                await OnSessionEndedAsync();
            });
        }
    }

    private async Task SendKeepAlivesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await Connection.SendAsync(KeepAlivePacket.Idle(), cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task<bool> HandshakeAsync(uint seed, CancellationToken cancellationToken)
    {
        while (await Connection.ReceiveRawAsync(cancellationToken) is { Length: > 0 } packet)
        {
            if (Log.IsEnabled(LogChannel.Packets)) Log.Debug(LogChannel.Packets, Tag, $"HANDSHAKE RECV {Log.Hex(packet)}");

            Account? account = packet[0] switch
            {
                ProtocolConstants.ClientLoginMarker when AcceptsLogin => await LoginAsync(packet, seed, cancellationToken),
                (byte)MsgCategory.cClientTransferInfo when AcceptsTransfer => await TransferAsync(packet, cancellationToken),
                _ => await RejectAsync(packet[0], cancellationToken),
            };
            if (account is null) continue;

            Account = account;
            await Connection.SendRawAsync(LoginReply.SessionInfo(account.SessionKey), cancellationToken);

            byte[]? ready = await Connection.ReceiveRawAsync(cancellationToken);
            if (ready is not [(byte)MsgCategory.cSessionReady, ..])
            {
                Log.Warn(Tag, $"Expected cSessionReady, got {(ready is null ? "disconnect" : Log.Hex(ready))}");
                return false;
            }

            await Connection.SendRawAsync(LoginReply.StartSession(), cancellationToken);
            Log.Info(LogChannel.Session, Tag, $"Session started (uid {account.Id}, nick '{account.Nickname}', grade {account.Grade})");
            return true;
        }
        return false;
    }

    private async Task<Account?> LoginAsync(byte[] packet, uint seed, CancellationToken cancellationToken)
    {
        ClientLoginInfo info;
        try
        {
            info = ClientLoginInfo.Parse(packet, seed);
        }
        catch (EndOfStreamException)
        {
            await Connection.SendRawAsync(LoginReply.Failed(NetError.Login_PacketVersion), cancellationToken);
            return null;
        }

        Log.Info(Tag, $"Login '{info.LoginId}' (packet v{info.PacketVersion}, locale '{info.Locale}')");

        if (info.PacketVersion != ProtocolConstants.PacketVersion)
            return await FailAsync(LoginReply.Failed(NetError.Login_PacketVersion), "packet version mismatch", cancellationToken);
        if (info.TokenMode)
            return await FailAsync(LoginReply.Failed(NetError.OTP_Failed), "token login is not supported", cancellationToken);

        Account? account = Accounts.FindByLoginId(info.LoginId);
        if (account is null)
        {
            NetError? invalid = AccountRules.ValidateNewAccount(info.LoginId, info.Password, Settings.NewAccounts.PasswordNeedsPunctuation);
            if (invalid is { } reason)
                return await FailAsync(LoginReply.CreateIdFailed(reason), $"registration refused: {reason}", cancellationToken);

            account = Accounts.Create(info.LoginId, PasswordHasher.Hash(info.Password));
            if (account is null)
                return await FailAsync(LoginReply.CreateIdFailed(NetError.ID_AlreadyExist), "id taken", cancellationToken);

            NewAccountSettings start = Settings.NewAccounts;
            if (info.Password != start.TestPassword)
            {
                account = Accounts.Update(account.Id, created =>
                {
                    created.Level = start.Level;
                    created.Experience = GameData.Instance.ExpForLevel(start.Level);
                    created.Gold = start.Gold;
                    created.Cash = start.Cash;
                }) ?? account;
            }

            Log.Info(Tag, $"Registered new account '{account.LoginId}' (uid {account.Id}, level {account.DisplayLevel})");
        }
        else if (!PasswordHasher.Verify(info.Password, account.PasswordHash))
        {
            if (++_wrongPasswords < AccountRules.MaxWrongPasswords)
                return await FailAsync(LoginReply.Failed(NetError.PW_WrongPassword), $"wrong password ({_wrongPasswords} of {AccountRules.MaxWrongPasswords})", cancellationToken);

            await FailAsync(LoginReply.Failed(NetError.PW_3Times_WrongPassword), "wrong password three times - disconnected", cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken); // let the reply leave before the socket closes
            Connection.Close();
            return null;
        }

        if (account.IsBanned)
        {
            uint seconds = (uint)Math.Clamp((account.BannedUntilUtc!.Value - DateTime.UtcNow).TotalSeconds, 1, uint.MaxValue);
            return await FailAsync(LoginReply.Failed(NetError.ID_ConnectionBlocked, seconds), $"banned until {account.BannedUntilUtc:u}", cancellationToken);
        }

        AccountGrade grade = account.Grade;
        if (Settings.Staff.FirstOrDefault(entry => entry.Key.Equals(account.LoginId, StringComparison.OrdinalIgnoreCase)) is { Key: not null } staff
            && Permissions.TryParseGrade(staff.Value, out AccountGrade configured))
        {
            grade = configured;
        }

        ulong sessionKey = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        return Accounts.Update(account.Id, a =>
        {
            a.SessionKey = sessionKey;
            a.LastLoginUtc = DateTime.UtcNow;
            a.Grade = grade;
        });
    }

    private async Task<Account?> TransferAsync(byte[] packet, CancellationToken cancellationToken)
    {
        ClientTransferInfo info;
        try
        {
            info = ClientTransferInfo.Parse(packet);
        }
        catch (EndOfStreamException)
        {
            return await FailAsync(LoginReply.Failed(NetError.ServerTransfer), "malformed transfer packet", cancellationToken);
        }

        Log.Info(Tag, $"Transfer '{info.LoginId}' / '{info.Nickname}'");

        Account? account = Accounts.FindByLoginId(info.LoginId);
        if (account is null || account.SessionKey == 0 || account.SessionKey != info.SessionKey)
            return await FailAsync(LoginReply.Failed(NetError.ServerTransfer), "unknown session key", cancellationToken);

        if (ServerChanges.Take(account.Id) is { } change)
        {
            ServerChangeChannel = change.ChannelId;
            Log.Info(LogChannel.Session, Tag, $"Server change of '{account.LoginId}' to channel {change.ChannelId} (key {change.Key:X8}, client sent {info.Channel} / {info.Key:X8})");
        }

        return account;
    }

    private async Task<Account?> RejectAsync(byte marker, CancellationToken cancellationToken)
        => await FailAsync(LoginReply.Failed(NetError.LoginFailed), $"unexpected handshake packet 0x{marker:X2}", cancellationToken);

    private async Task<Account?> FailAsync(byte[] reply, string reason, CancellationToken cancellationToken)
    {
        Log.Warn(Tag, $"Handshake refused: {reason}");
        await Connection.SendRawAsync(reply, cancellationToken);
        return null;
    }
}
