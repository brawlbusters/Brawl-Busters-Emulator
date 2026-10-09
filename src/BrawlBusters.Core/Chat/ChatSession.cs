using System.Text;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Chat;

public sealed class ChatSession
{
    private static readonly byte[] Greeting = Encoding.ASCII.GetBytes(ProtocolConstants.ChatGreeting + "\0");

    private readonly GameConnection _connection;
    private readonly EmulatorSettings _settings;
    private readonly ChatHub _hub;

    public ChatSession(GameConnection connection, EmulatorSettings settings, ChatHub hub)
    {
        _connection = connection;
        _settings = settings;
        _hub = hub;
    }

    public uint UserId { get; private set; }
    public string Nickname { get; private set; } = "";

    public ushort? Room { get; set; }

    public (ushort Channel, ushort Room)? GameRoom { get; set; }

    public uint GameRoomKey { get; set; }

    public byte Presence { get; set; }

    public ushort PresenceDetail { get; set; }

    public string Tag => $"{_connection.Tag} {Nickname}";

    public Task SendAsync(PacketWriter message, CancellationToken cancellationToken = default)
    {
        if (Log.IsEnabled(LogChannel.Packets))
        {
            byte[] bytes = message.ToArray();
            Log.Debug(LogChannel.Packets, Tag, $"CHAT SEND {(MsgCategory)bytes[0],-10} {Log.Hex(bytes.AsSpan(1))}");
        }
        return _connection.SendAsync(message, cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _connection.SendRawAsync(Greeting, cancellationToken);

        byte[]? info = await _connection.ReceiveRawAsync(cancellationToken);
        if (info is not [(byte)MsgCategory.cClientInfo_Chatter, ..])
        {
            Log.Warn(_connection.Tag, $"Chat: unexpected first packet {(info is null ? "(closed)" : Log.Hex(info))}");
            return;
        }

        var reader = new PacketReader(info, offset: 1);
        ushort version = reader.ReadUInt16();
        UserId = reader.ReadUInt32();
        Nickname = reader.ReadWideString();
        ClientSession? game = SessionRegistry.Find(UserId);
        if (game is null || !game.Account.Nickname.Equals(Nickname, StringComparison.Ordinal)
            || !game.Connection.RemoteEndPoint.Address.Equals(_connection.RemoteEndPoint.Address))
        {
            Log.Warn(LogChannel.Chat, _connection.Tag, $"Chat login as uid {UserId} / '{Nickname}' refused: no matching game session from this address");
            return;
        }
        Log.Info(LogChannel.Chat, Tag, $"Chat login: uid {UserId} (v{version})");

        await _connection.SendRawAsync(LoginReply.SessionInfo(UserId), cancellationToken);
        if (await _connection.ReceiveRawAsync(cancellationToken) is not [(byte)MsgCategory.cSessionReady, ..]) return;
        await _connection.SendRawAsync(LoginReply.StartSession(), cancellationToken);

        await World.RunAsync(() =>
        {
            _hub.Connected(this);
            return Task.CompletedTask;
        });
        try
        {
            while (await _connection.ReceiveAsync(cancellationToken) is { } message)
                await World.RunAsync(() => _hub.HandleAsync(this, message, cancellationToken));
        }
        finally
        {
            await World.RunAsync(() =>
            {
                _hub.Disconnected(this);
                return Task.CompletedTask;
            });
        }
    }
}
