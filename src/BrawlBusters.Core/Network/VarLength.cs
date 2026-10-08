namespace BrawlBusters.Core.Network;

public static class VarLength
{
    public const int MaxFrameSize = 0xA00000;

    public static int SizeOf(uint value) => value switch
    {
        <= 0x3F => 1,
        <= 0x3FFF => 2,
        <= 0x3FFFFFFF => 4,
        _ => 5,
    };

    public static int Write(Span<byte> destination, uint value)
    {
        switch (SizeOf(value))
        {
            case 1:
                destination[0] = (byte)value;
                return 1;
            case 2:
                destination[0] = (byte)(0x40 | (value >> 8));
                destination[1] = (byte)value;
                return 2;
            case 4:
                destination[0] = (byte)(0x80 | (value >> 24));
                destination[1] = (byte)(value >> 16);
                destination[2] = (byte)(value >> 8);
                destination[3] = (byte)value;
                return 4;
            default:
                destination[0] = 0xC0;
                BitConverter.TryWriteBytes(destination[1..], value);
                return 5;
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out uint value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (source.IsEmpty) return false;

        byte first = source[0];
        switch (first & 0xC0)
        {
            case 0x00:
                value = first;
                consumed = 1;
                return true;
            case 0x40:
                if (source.Length < 2) return false;
                value = (uint)((first & 0x3F) << 8 | source[1]);
                consumed = 2;
                return true;
            case 0x80:
                if (source.Length < 4) return false;
                value = (uint)((first & 0x3F) << 24 | source[1] << 16 | source[2] << 8 | source[3]);
                consumed = 4;
                return true;
            default:
                if (source.Length < 5) return false;
                value = BitConverter.ToUInt32(source.Slice(1, 4));
                consumed = 5;
                return true;
        }
    }
}
