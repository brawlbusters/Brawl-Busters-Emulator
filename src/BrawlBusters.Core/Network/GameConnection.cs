using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Network;

public sealed class GameConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private const int OutgoingLimit = 4096;

    private readonly object _sendGate = new();
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private int _queued;
    private readonly Queue<byte[]> _pendingMessages = new();
    private readonly byte[] _receiveBuffer = new byte[64 * 1024];
    private byte[] _frameBuffer = new byte[4096];
    private int _frameBufferLength;
    private byte _sendSequence;
    private byte _receiveSequence;

    public GameConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        RemoteEndPoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        LocalPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        Id = Interlocked.Increment(ref _nextId);
        _writer = Task.Run(WriteLoopAsync);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (byte[] data in _outgoing.Reader.ReadAllAsync())
            {
                Interlocked.Decrement(ref _queued);
                await _stream.WriteAsync(data);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            _outgoing.Writer.TryComplete();
            Close();
        }
    }

    private void Enqueue(byte[] data)
    {
        if (Interlocked.Increment(ref _queued) > OutgoingLimit)
        {
            Log.Warn(LogChannel.Network, Tag, $"More than {OutgoingLimit} messages waiting to be sent - the connection is closed");
            Close();
            return;
        }
        _outgoing.Writer.TryWrite(data);
    }

    public void Close()
    {
        try
        {
            _client.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    private static int _nextId;

    public int Id { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public int LocalPort { get; }
    public string Tag => $"#{Id} {RemoteEndPoint}";

    public Task SendRawAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        lock (_sendGate) Enqueue(data);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveRawAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            int read = await _stream.ReadAsync(_receiveBuffer, cancellationToken);
            return read <= 0 ? null : _receiveBuffer.AsSpan(0, read).ToArray();
        }
        catch (ObjectDisposedException)
        {
            // The server closed this connection itself (kick, second login, third wrong password).
            return null;
        }
    }

    public Task SendAsync(PacketWriter message, CancellationToken cancellationToken = default)
        => SendAsync(message.ToArray(), cancellationToken);

    public Task SendAsync(byte[] message, CancellationToken cancellationToken = default)
    {
        lock (_sendGate)
        {
            var plain = new byte[message.Length + 2];
            plain[0] = ProtocolConstants.SessionSingle;
            plain[1] = _sendSequence++;
            message.CopyTo(plain, 2);

            byte[] compressed = Lzf.Compress(plain);
            var frame = new byte[VarLength.SizeOf((uint)compressed.Length) + compressed.Length];
            int headerSize = VarLength.Write(frame, (uint)compressed.Length);
            compressed.CopyTo(frame, headerSize);
            Enqueue(frame);
        }
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_pendingMessages.Count > 0) return _pendingMessages.Dequeue();

            if (TryTakeFrame(out byte[]? frame))
            {
                UnpackSessionFrame(Lzf.Decompress(frame));
                continue;
            }

            int read;
            try
            {
                read = await _stream.ReadAsync(_receiveBuffer, cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // The server closed this connection itself (kick, second login, server change).
                return null;
            }
            if (read <= 0) return null;

            if (_frameBufferLength + read > _frameBuffer.Length)
                Array.Resize(ref _frameBuffer, Math.Max(_frameBuffer.Length * 2, _frameBufferLength + read));
            Buffer.BlockCopy(_receiveBuffer, 0, _frameBuffer, _frameBufferLength, read);
            _frameBufferLength += read;
        }
    }

    private bool TryTakeFrame(out byte[] frame)
    {
        frame = [];
        var buffered = _frameBuffer.AsSpan(0, _frameBufferLength);
        if (!VarLength.TryRead(buffered, out uint size, out int headerSize)) return false;
        if (size > VarLength.MaxFrameSize) throw new InvalidDataException($"Frame of {size} bytes exceeds the protocol limit.");
        if (buffered.Length < headerSize + (int)size) return false;

        frame = buffered.Slice(headerSize, (int)size).ToArray();
        int consumed = headerSize + (int)size;
        buffered[consumed..].CopyTo(_frameBuffer);
        _frameBufferLength -= consumed;
        return true;
    }

    private void UnpackSessionFrame(byte[] plain)
    {
        if (plain.Length < 2) throw new InvalidDataException("Session frame shorter than its header.");

        byte type = plain[0];
        byte sequence = plain[1];
        if (sequence != _receiveSequence)
            Log.Warn(Tag, $"Sequence mismatch: got {sequence}, expected {_receiveSequence}.");
        _receiveSequence = (byte)(sequence + 1);

        switch (type)
        {
            case ProtocolConstants.SessionSingle:
                _pendingMessages.Enqueue(plain.AsSpan(2).ToArray());
                break;

            case ProtocolConstants.SessionBatch:
                UnpackBatch(plain.AsSpan(2));
                break;

            default:
                throw new InvalidDataException($"Unknown session message type 0x{type:X2}.");
        }
    }

    private void UnpackBatch(ReadOnlySpan<byte> body)
    {
        if (!VarLength.TryRead(body, out uint count, out int offset))
            throw new InvalidDataException("Truncated batch header.");

        int sizesOffset = offset;
        int dataOffset = offset + (int)count * 2;
        for (int i = 0; i < count; i++)
        {
            int size = BitConverter.ToUInt16(body.Slice(sizesOffset + i * 2, 2));
            _pendingMessages.Enqueue(body.Slice(dataOffset, size).ToArray());
            dataOffset += size;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(2)));
        try
        {
            await _stream.DisposeAsync();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        _client.Dispose();
    }
}
