using System.Security.Cryptography;

namespace BrawlBusters.Core.Security;

public static class PasswordCipher
{
    public static byte[] Decrypt(uint seed, ReadOnlySpan<byte> cipherText)
    {
        byte[] key = MD5.HashData(BitConverter.GetBytes(seed));
        return Rc4(key, cipherText);
    }

    private static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < 256; i++) state[i] = (byte)i;

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + state[i]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
            output[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 0xFF]);
        }
        return output;
    }
}
