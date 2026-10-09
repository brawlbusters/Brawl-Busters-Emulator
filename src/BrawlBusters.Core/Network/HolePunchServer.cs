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

    private readonly string _name;
    private readonly IPEndPoint _endPoint;

    public HolePunchServer(string name, IPEndPoint endPoint)
    {
        _name = name;
        _endPoint = endPoint;
    }

    public static IPEndPoint? FindPlayerEndPoint(uint userId)
        => PlayerEndPoints.TryGetValue(userId, out IPEndPoint? endPoint) ? endPoint : null;

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
        RelayEndPoints.TryGetValue(first, out IPEndPoint? firstEnd);
        RelayEndPoints.TryGetValue(second, out IPEndPoint? secondEnd);

        IPEndPoint? target = datagram.RemoteEndPoint.Equals(firstEnd) ? secondEnd
            : datagram.RemoteEndPoint.Equals(secondEnd) ? firstEnd
            : null;
        if (target is null)
        {
            if (Interlocked.Increment(ref _relayDrops) <= RelayDropLogLimit)
                Log.Warn(_name, $"UDP relay data between {first} and {second} from {datagram.RemoteEndPoint} dropped: " +
                                $"{(firstEnd is null || secondEnd is null ? "one side has not registered with the relay" : "the sender is neither side")}");
            return;
        }

        await socket.SendAsync(packet, target, cancellationToken);
        long forwarded = Interlocked.Increment(ref _relayForwarded);
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
