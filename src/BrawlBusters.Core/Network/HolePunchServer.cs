using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Network;

public sealed class HolePunchServer
{
    private const int HeaderLength = 6;
    private const byte ServerPunchRequest = 0x02;
    private const byte ServerPunchAccepted = 0x03;
    private const byte ServerPunchKeepAlive = 0x04;
    private const byte RelayPunchRequest = 0x0B;
    private const byte RelayPunchKeepAlive = 0x0C;
    private const byte RelayPunchAccepted = 0x0E;

    private const byte RelayData = 0x0D;
    private const byte PingRequest = 0x1D;
    private const byte PingReply = 0x1E;
    private const int PingPayloadLength = 8;
    private const int RelayHeaderLength = 10;
    private const int RelayDropLogLimit = 20;
    private const int RelayLogEvery = 5000;

    private static readonly ConcurrentDictionary<uint, IPEndPoint> RelayEndPoints = new();
    private long _relayForwarded;
    private long _relayDrops;

    private static readonly ConcurrentDictionary<uint, IPEndPoint> PlayerEndPoints = new();

    private static readonly ConcurrentDictionary<uint, IPEndPoint> PlayerLocalEndPoints = new();

    /// <summary>The server socket each player punched at login: the one port its firewall and router let answers in from.</summary>
    private static readonly ConcurrentDictionary<uint, UdpClient> PlayerSockets = new();
    private const int RelayDetailLogLimit = 200;
    private const int RelayHexLogLimit = 12;

    private readonly string _name;
    private readonly IPEndPoint _endPoint;

    public HolePunchServer(string name, IPEndPoint endPoint)
    {
        _name = name;
        _endPoint = endPoint;
    }

    public static IPEndPoint? FindPlayerEndPoint(uint userId)
        => PlayerEndPoints.TryGetValue(userId, out IPEndPoint? endPoint) ? endPoint : null;

    /// <summary>The server UDP port a player punched at login - the one port its firewall and router let traffic in from.</summary>
    public static int? FindPunchedPort(uint userId)
        => PlayerSockets.TryGetValue(userId, out UdpClient? socket) && socket.Client.LocalEndPoint is IPEndPoint local ? local.Port : null;

    public static IPEndPoint? FindPlayerLocalEndPoint(uint userId)
        => PlayerLocalEndPoints.TryGetValue(userId, out IPEndPoint? endPoint) ? endPoint : null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(_endPoint);
        Log.Info(_name, $"UDP listening on {_endPoint}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult datagram;
                try
                {
                    datagram = await socket.ReceiveAsync(cancellationToken);
                }
                catch (SocketException)
                {
                    continue;
                }

                await HandleAsync(socket, datagram, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ForwardAsync(UdpClient socket, UdpReceiveResult datagram, uint first, CancellationToken cancellationToken)
    {
        byte[] packet = datagram.Buffer;
        if (packet.Length < RelayHeaderLength)
        {
            Log.Warn(_name, $"UDP relay data from {datagram.RemoteEndPoint} too short: {Log.Hex(packet)}");
            return;
        }

        uint second = BitConverter.ToUInt32(packet, HeaderLength);
        // The header names the side that opened the connection and the host, in that order, in both directions -
        // not the sender. A player normally registers with the relay (sub 0B) when a room tells it the relay
        // address; ranked rooms carry no such part (the address only comes with sGame 00) and their players never
        // register. Such a player is reached on the address it punched the server from: the client uses one
        // socket for everything.
        // The address of the latest login wins over an older relay registration: a restarted client has a new port.
        if (!PlayerEndPoints.TryGetValue(first, out IPEndPoint? firstEnd)) RelayEndPoints.TryGetValue(first, out firstEnd);
        if (!PlayerEndPoints.TryGetValue(second, out IPEndPoint? secondEnd)) RelayEndPoints.TryGetValue(second, out secondEnd);
        bool fromFirst = datagram.RemoteEndPoint.Equals(firstEnd) || (RelayEndPoints.TryGetValue(first, out IPEndPoint? a) && datagram.RemoteEndPoint.Equals(a));
        bool fromSecond = datagram.RemoteEndPoint.Equals(secondEnd) || (RelayEndPoints.TryGetValue(second, out IPEndPoint? b) && datagram.RemoteEndPoint.Equals(b));

        IPEndPoint? target = fromFirst ? secondEnd : fromSecond ? firstEnd : null;
        if (target is null)
        {
            if (Interlocked.Increment(ref _relayDrops) <= RelayDropLogLimit)
                Log.Warn(_name, $"UDP relay data between {first} and {second} from {datagram.RemoteEndPoint} dropped: " +
                                $"{(firstEnd is null || secondEnd is null ? "one side is not known to the relay" : "the sender is neither side")}");
            return;
        }

        // A player that never registered with this relay port has no opening for it in its firewall or router -
        // only for the server port it punched at login. Such a player is sent to from that port's socket.
        uint targetId = fromFirst ? second : first;
        UdpClient via = RelayEndPoints.ContainsKey(targetId) || !PlayerSockets.TryGetValue(targetId, out UdpClient? punched) ? socket : punched;
        // The relay header is for the relay only. The receiving client has no handler for relay data as such (its
        // handler for type 58 takes the registration answer 0E and nothing else, client 0x5B58A0): what it expects
        // is the packet that was wrapped - a player-to-player packet (type 56) whose own sub-type says it came
        // through the relay (0C), which it answers with a wrapped packet of its own (client 0x4F97E2).
        ReadOnlyMemory<byte> inner = packet.AsMemory(RelayHeaderLength);
        await via.SendAsync(inner, target, cancellationToken);
        long forwarded = Interlocked.Increment(ref _relayForwarded);
        if (forwarded <= RelayDetailLogLimit)
            Log.Debug(_name, $"UDP relay {datagram.RemoteEndPoint} -> {target} ({first} <-> {second}, {packet.Length} bytes{(via == socket ? "" : ", through the port the receiver punched")}){(forwarded <= RelayHexLogLimit ? " " + Log.Hex(packet) : "")}");
        if (forwarded == 1 || forwarded % RelayLogEvery == 0)
            Log.Info(_name, $"UDP relay: {forwarded} packet(s) forwarded (latest {first} <-> {second}, {packet.Length} bytes)");
    }

    private async Task HandleAsync(UdpClient socket, UdpReceiveResult datagram, CancellationToken cancellationToken)
    {
        byte[] packet = datagram.Buffer;
        if (packet.Length < HeaderLength)
        {
            Log.Warn(_name, $"UDP RECV {datagram.RemoteEndPoint} too short: {Log.Hex(packet)}");
            return;
        }

        byte type = packet[0];
        uint key = BitConverter.ToUInt32(packet, 1);
        byte sub = packet[5];

        if (sub is RelayPunchRequest or RelayPunchKeepAlive) RelayEndPoints[key] = datagram.RemoteEndPoint;
        if (sub is ServerPunchKeepAlive or RelayPunchKeepAlive) return;

        if (sub == RelayData)
        {
            await ForwardAsync(socket, datagram, key, cancellationToken);
            return;
        }

        if (sub == PingRequest && packet.Length >= HeaderLength + PingPayloadLength)
        {
            byte[] pong = packet.AsSpan(0, HeaderLength + PingPayloadLength).ToArray();
            pong[5] = PingReply;
            await socket.SendAsync(pong, datagram.RemoteEndPoint, cancellationToken);
            return;
        }

        Log.Debug(_name, $"UDP RECV {datagram.RemoteEndPoint} {Log.Hex(packet)}");
        PlayerEndPoints[key] = datagram.RemoteEndPoint;
        PlayerSockets[key] = socket;
        if (sub == ServerPunchRequest && packet.Length >= HeaderLength + 6)
            PlayerLocalEndPoints[key] = new PacketReader(packet, HeaderLength).ReadEndPoint();

        byte[]? reply = sub switch
        {
            ServerPunchRequest => [type, packet[1], packet[2], packet[3], packet[4], ServerPunchAccepted],
            RelayPunchRequest => [type, packet[1], packet[2], packet[3], packet[4], RelayPunchAccepted, 1],
            _ => null,
        };

        if (reply is null)
        {
            Log.Warn(_name, $"UDP sub-type 0x{sub:X2} not handled");
            return;
        }

        await socket.SendAsync(reply, datagram.RemoteEndPoint, cancellationToken);
        Log.Debug(_name, $"UDP SEND {datagram.RemoteEndPoint} {Log.Hex(reply)}");
    }
}
