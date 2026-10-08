using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Network;

public sealed class PacketWriter
{
    private readonly MemoryStream _stream = new();

    public PacketWriter()
    {
    }

    public PacketWriter(MsgCategory category) => WriteByte((byte)category);

    public PacketWriter(MsgCategory category, byte subId) : this(category) => WriteByte(subId);

    public int Length => (int)_stream.Length;

    public PacketWriter WriteByte(byte value)
    {
        _stream.WriteByte(value);
        return this;
    }

    public PacketWriter WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public PacketWriter WriteUInt16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public PacketWriter WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public PacketWriter WriteUInt64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    public PacketWriter WriteBytes(ReadOnlySpan<byte> value)
    {
        _stream.Write(value);
        return this;
    }

    public PacketWriter WriteZeros(int count)
    {
        _stream.Write(new byte[count]);
        return this;
    }

    public PacketWriter WriteString(string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        return WriteUInt16((ushort)bytes.Length).WriteBytes(bytes);
    }

    public PacketWriter WriteWideString(string value)
    {
        return WriteUInt16((ushort)value.Length).WriteBytes(Encoding.Unicode.GetBytes(value));
    }

    public PacketWriter WriteEndPoint(IPEndPoint endPoint)
    {
        if (endPoint.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The client only understands IPv4 endpoints.", nameof(endPoint));

        uint networkOrder = BitConverter.ToUInt32(endPoint.Address.GetAddressBytes());
        uint hostOrder = BinaryPrimitives.ReverseEndianness(networkOrder);
        return WriteUInt32(~hostOrder).WriteUInt16((ushort)~endPoint.Port);
    }

    public byte[] ToArray() => _stream.ToArray();
}
