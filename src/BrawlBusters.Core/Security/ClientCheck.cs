using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Security;

public enum ClientCheckResult
{
    /// <summary>Nothing was heard from this address: the client runs without the check module.</summary>
    Missing,

    /// <summary>The module answered, and the game tables it read are not the ones this server allows.</summary>
    Modified,

    Verified,
}

/// <summary>
/// Proof that a player's game tables (Data/xmandb.bus: character, weapon, damage, monster and rule tables) are the
/// unchanged ones. The client itself sends nothing of the kind - the original game left that to a third-party
/// anti-cheat - so the proof comes from a small module the game loads by itself, tools/client_check (shipped as
/// bin/LightFX.dll, a library the client looks for at start and does not need).
///
/// The module asks for a challenge on the lobby's UDP port, and answers with SHA-256(challenge + digest of the
/// tables + salt). The digest covers every file of the archive except the server address table, which differs from
/// one installation to the next. The challenge makes an answer worthless for anybody who recorded it. It is repeated
/// every minute while the game runs; the result is kept per address.
///
/// This stops players who change the tables with an unpack tool. It does not stop somebody who can take the module
/// apart: it runs on the player's own computer, like everything else there.
/// </summary>
public static class ClientCheck
{
    public const string ModeOff = "off";
    public const string ModeLog = "log";
    public const string ModeRequire = "require";

    private const byte AskChallenge = 1;
    private const byte Challenge = 2;
    private const byte Proof = 3;
    private const byte Verdict = 4;
    private const int NonceLength = 16;
    private const int HashLength = 32;

    private static readonly byte[] Magic = "BBIC"u8.ToArray();
    private static readonly byte[] Salt = "BrawlBusters client check v1"u8.ToArray();
    private const string SkippedFile = "clientconfigdb";

    private static readonly ConcurrentDictionary<IPAddress, byte[]> Nonces = new();
    private static readonly ConcurrentDictionary<IPAddress, (DateTime At, bool Ok)> Results = new();
    private static readonly List<byte[]> Allowed = [];
    private static AntiCheatSettings _settings = new();

    public static string Mode => _settings.ClientCheck.ToLowerInvariant() switch
    {
        ModeRequire => ModeRequire,
        ModeOff => ModeOff,
        _ => ModeLog,
    };

    /// <summary>Reads the table digests this server allows: of its own copy of the client's archive and the listed ones.</summary>
    public static void Configure(AntiCheatSettings settings, string baseDirectory)
    {
        _settings = settings;
        Allowed.Clear();
        if (Mode == ModeOff) return;

        string path = Path.GetFullPath(Path.Combine(baseDirectory, settings.ClientDataFile));
        if (File.Exists(path) && Digest(File.ReadAllBytes(path)) is { } own)
        {
            Allowed.Add(own);
            Log.Info("ClientCheck", $"Game tables of {path}: digest {Convert.ToHexString(own)[..16]}...");
        }
        foreach (string listed in settings.AllowedClientDigests)
        {
            try
            {
                byte[] digest = Convert.FromHexString(listed.Trim());
                if (digest.Length == HashLength) Allowed.Add(digest);
            }
            catch (FormatException)
            {
                Log.Warn("ClientCheck", $"AllowedClientDigests: '{listed}' is not a digest (64 hex digits)");
            }
        }

        if (Allowed.Count == 0)
            Log.Warn("ClientCheck", $"No game tables to compare with ({path} not found, AllowedClientDigests empty) - no client can be verified");
        Log.Info("ClientCheck", $"Mode '{Mode}': " + Mode switch
        {
            ModeRequire => "a login without a verified client is refused",
            _ => "a login without a verified client is logged, not refused",
        });
    }

    /// <summary>
    /// The digest of an archive's files, in the order of its table: name, size and stored bytes of each, without the
    /// server address table. Null when the bytes are not an archive.
    /// </summary>
    public static byte[]? Digest(byte[] archive)
    {
        const int headerOffset = 0x21, headerSize = 0x110, entrySize = 0x114, nameSize = 0x108;
        if (archive.Length < headerOffset + headerSize) return null;

        byte[] key = Key();
        byte[] header = Xor(archive.AsSpan(headerOffset, headerSize), key);
        uint count = BitConverter.ToUInt32(header, nameSize + 4);
        long table = headerOffset + headerSize;
        long payloads = table + (long)count * entrySize;
        if (count == 0 || count > 100_000 || payloads > archive.Length) return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < count; i++)
        {
            byte[] entry = Xor(archive.AsSpan((int)(table + i * entrySize), entrySize), key);
            int nameLength = Array.IndexOf(entry, (byte)0, 0, nameSize);
            if (nameLength < 0) nameLength = nameSize;
            uint offset = BitConverter.ToUInt32(entry, nameSize);
            uint size = BitConverter.ToUInt32(entry, nameSize + 4);
            if (payloads + offset + size > archive.Length) return null;
            if (Encoding.Latin1.GetString(entry, 0, nameLength).Contains(SkippedFile, StringComparison.OrdinalIgnoreCase)) continue;

            hash.AppendData(entry, 0, nameLength);
            hash.AppendData(BitConverter.GetBytes(size));
            hash.AppendData(archive, (int)(payloads + offset), (int)size);
        }
        return hash.GetHashAndReset();
    }

    /// <summary>The answer to a datagram of the check module, or null when the datagram is something else.</summary>
    public static byte[]? TryHandle(byte[] packet, IPEndPoint from)
    {
        if (packet.Length < Magic.Length + 1 || !packet.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        if (Mode == ModeOff) return [];

        IPAddress address = from.Address.MapToIPv4();
        switch (packet[Magic.Length])
        {
            case AskChallenge:
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
                Nonces[address] = nonce;
                return [.. Magic, Challenge, .. nonce];
            }
            case Proof when packet.Length >= Magic.Length + 1 + HashLength && Nonces.TryRemove(address, out byte[]? asked):
            {
                ReadOnlySpan<byte> proof = packet.AsSpan(Magic.Length + 1, HashLength);
                bool ok = false;
                foreach (byte[] digest in Allowed)
                    ok |= CryptographicOperations.FixedTimeEquals(proof, SHA256.HashData([.. asked, .. digest, .. Salt]));

                bool before = Results.TryGetValue(address, out var last) && last.Ok;
                Results[address] = (DateTime.UtcNow, ok);
                if (!ok) Log.Warn("ClientCheck", $"{address}: the game tables of this client are MODIFIED (or of another version)");
                else if (!before) Log.Info("ClientCheck", $"{address}: game tables verified");
                return [.. Magic, Verdict, (byte)(ok ? 1 : 0)];
            }
            default:
                return [];
        }
    }

    public static ClientCheckResult Of(IPAddress address)
    {
        if (!Results.TryGetValue(address.MapToIPv4(), out var last)) return ClientCheckResult.Missing;
        if (!last.Ok) return ClientCheckResult.Modified;
        return (DateTime.UtcNow - last.At).TotalSeconds <= _settings.ClientCheckMaxAgeSeconds ? ClientCheckResult.Verified : ClientCheckResult.Missing;
    }

    private static byte[] Xor(ReadOnlySpan<byte> data, byte[] key)
    {
        byte[] plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) plain[i] = (byte)(data[i] ^ key[i % key.Length]);
        return plain;
    }

    /// <summary>The archive's XOR key: 4096 bytes of mt19937(seed 0x6D2C1B76), each (value / 16384) % 105 (client 0x50AEA3).</summary>
    private static byte[] Key()
    {
        const int n = 624;
        uint[] state = new uint[n];
        state[0] = 0x6D2C1B76;
        for (uint i = 1; i < n; i++) state[i] = 1812433253u * (state[i - 1] ^ (state[i - 1] >> 30)) + i;

        byte[] key = new byte[0x1000];
        int index = n;
        for (int produced = 0; produced < key.Length; produced++)
        {
            if (index >= n)
            {
                for (int k = 0; k < n; k++)
                {
                    uint mixed = (state[k] & 0x80000000) | (state[(k + 1) % n] & 0x7FFFFFFF);
                    state[k] = state[(k + 397) % n] ^ (mixed >> 1) ^ ((mixed & 1) != 0 ? 0x9908B0DFu : 0);
                }
                index = 0;
            }
            uint y = state[index++];
            y ^= y >> 11;
            y ^= (y << 7) & 0x9D2C5680;
            y ^= (y << 15) & 0xEFC60000;
            y ^= y >> 18;
            key[produced] = (byte)(y / 16384 % 105);
        }
        return key;
    }
}
