namespace BrawlBusters.Core.Network;

public static class Lzf
{
    private const int HashBits = 14;
    private const int HashSize = 1 << HashBits;
    private const int MaxLiteral = 32;
    private const int MaxOffset = 1 << 13;
    private const int MaxMatch = 264;
    private const int MinMatch = 3;

    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length + input.Length / MaxLiteral + 2];
        var table = new int[HashSize];
        Array.Fill(table, -1);

        int inPos = 0, outPos = 0, literalStart = 0;

        while (inPos < input.Length)
        {
            int matchLength = 0, matchOffset = 0;

            if (inPos + MinMatch <= input.Length)
            {
                int hash = Hash(input, inPos);
                int candidate = table[hash];
                table[hash] = inPos;

                if (candidate >= 0 && inPos - candidate <= MaxOffset)
                {
                    int limit = Math.Min(MaxMatch, input.Length - inPos);
                    int length = 0;
                    while (length < limit && input[candidate + length] == input[inPos + length]) length++;
                    if (length >= MinMatch)
                    {
                        matchLength = length;
                        matchOffset = inPos - candidate - 1;
                    }
                }
            }

            if (matchLength == 0)
            {
                inPos++;
                continue;
            }

            FlushLiterals(input, literalStart, inPos, output, ref outPos);

            int encodedLength = matchLength - 2;
            if (encodedLength < 7)
            {
                output[outPos++] = (byte)((encodedLength << 5) | (matchOffset >> 8));
            }
            else
            {
                output[outPos++] = (byte)((7 << 5) | (matchOffset >> 8));
                output[outPos++] = (byte)(encodedLength - 7);
            }
            output[outPos++] = (byte)matchOffset;

            inPos += matchLength;
            literalStart = inPos;
        }

        FlushLiterals(input, literalStart, inPos, output, ref outPos);
        return output.AsSpan(0, outPos).ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(Math.Max(64, input.Length * 2));
        int inPos = 0;

        while (inPos < input.Length)
        {
            int control = input[inPos++];
            if (control < MaxLiteral)
            {
                int count = control + 1;
                if (inPos + count > input.Length) throw new InvalidDataException("LZF literal run overruns input.");
                for (int i = 0; i < count; i++) output.Add(input[inPos++]);
                continue;
            }

            int length = control >> 5;
            if (length == 7)
            {
                if (inPos >= input.Length) throw new InvalidDataException("LZF match length overruns input.");
                length += input[inPos++];
            }
            if (inPos >= input.Length) throw new InvalidDataException("LZF match offset overruns input.");

            int reference = output.Count - ((control & 0x1F) << 8) - input[inPos++] - 1;
            if (reference < 0) throw new InvalidDataException("LZF back-reference before start of output.");

            for (int i = 0; i < length + 2; i++) output.Add(output[reference + i]);
        }

        return output.ToArray();
    }

    private static int Hash(ReadOnlySpan<byte> data, int position)
    {
        uint value = (uint)(data[position] << 16 | data[position + 1] << 8 | data[position + 2]);
        return (int)((value * 2654435761u) >> (32 - HashBits));
    }

    private static void FlushLiterals(ReadOnlySpan<byte> input, int start, int end, byte[] output, ref int outPos)
    {
        while (start < end)
        {
            int count = Math.Min(MaxLiteral, end - start);
            output[outPos++] = (byte)(count - 1);
            input.Slice(start, count).CopyTo(output.AsSpan(outPos));
            outPos += count;
            start += count;
        }
    }
}
