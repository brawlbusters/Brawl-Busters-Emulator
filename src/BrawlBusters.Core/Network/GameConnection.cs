using System.Net;
using System.Net.Sockets;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Network;

public sealed class GameConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
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
        Id = Interlocked.Increment(ref _nextId);
    }

    private static int _nextId;

    public int Id { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public string Tag => $"#{Id} {RemoteEndPoint}";

    public async Task SendRawAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(data, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<byte[]?> ReceiveRawAsync(CancellationToken cancellationToken = default)
    {
        int read = await _stream.ReadAsync(_receiveBuffer, cancellationToken);
        return read <= 0 ? null : _receiveBuffer.AsSpan(0, read).ToArray();
    }

    public Task SendAsync(PacketWriter message, CancellationToken cancellationToken = default)
        => SendAsync(message.ToArray(), cancellationToken);

    public async Task SendAsync(byte[] message, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var plain = new byte[message.Length + 2];
            plain[0] = ProtocolConstants.SessionSingle;
            plain[1] = _sendSequence++;
            message.CopyTo(plain, 2);

            byte[] compressed = Lzf.Compress(plain);
            var frame = new byte[VarLength.SizeOf((uint)compressed.Length) + compressed.Length];
            int headerSize = VarLength.Write(frame, (uint)compressed.Length);
            compressed.CopyTo(frame, headerSize);

            await _stream.WriteAsync(frame, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
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

            int read = await _stream.ReadAsync(_receiveBuffer, cancellationToken);
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
        try
        {
            await _stream.DisposeAsync();
        }
        catch (IOException)
        {
        }
        _client.Dispose();
        _sendLock.Dispose();
    }
}
