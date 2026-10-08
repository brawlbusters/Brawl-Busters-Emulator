using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace BrawlBusters.Core.Network;

public sealed class PacketReader
{
    private readonly byte[] _buffer;
    private int _position;

    public PacketReader(byte[] buffer, int offset = 0)
    {
        _buffer = buffer;
        _position = offset;
    }

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;
    public bool EndOfData => Remaining <= 0;

    public byte ReadByte() => Take(1)[0];
    public bool ReadBool() => ReadByte() != 0;
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public byte[] ReadBlob() => ReadBytes(ReadUInt16());

    public string ReadString() => Encoding.Latin1.GetString(Take(ReadUInt16()));

    public string ReadWideString() => Encoding.Unicode.GetString(Take(ReadUInt16() * 2));

    public IPEndPoint ReadEndPoint()
    {
        uint hostOrder = ~ReadUInt32();
        ushort port = (ushort)~ReadUInt16();
        return new IPEndPoint(new IPAddress(BinaryPrimitives.ReverseEndianness(hostOrder)), port);
    }

    public byte[] ReadToEnd() => ReadBytes(Remaining);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new EndOfStreamException($"Packet underrun: wanted {count} byte(s), {Remaining} left.");

        var span = _buffer.AsSpan(_position, count);
        _position += count;
        return span;
    }
}
