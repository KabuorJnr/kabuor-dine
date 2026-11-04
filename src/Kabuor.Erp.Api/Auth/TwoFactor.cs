// Two-step sign-in with any authenticator app (RFC 6238 TOTP: 6 digits, 30 seconds, SHA-1, as the apps expect),
// one-time recovery codes, and encryption of the stored secret with a key derived from the server's signing key.
using System.Security.Cryptography;
using System.Text;

namespace Kabuor.Erp.Auth;

public static class Totp
{
    public const int Step = 30;
    public static long StepNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Step;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static string Code(string base32Secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(Base32.Decode(base32Secret), counter);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    /// <summary>The step the code belongs to (allowing one step of clock drift either way), or null.</summary>
    public static long? Match(string base32Secret, string code, long lastUsedStep)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6) return null;
        var now = StepNow();
        for (var s = now - 1; s <= now + 1; s++)
            if (s > lastUsedStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(base32Secret, s)), Encoding.ASCII.GetBytes(code)))
                return s;
        return null;
    }

    public static string Uri(string base32Secret, string account, string issuer = "Kabuor ERP") =>
        $"otpauth://totp/{System.Uri.EscapeDataString($"{issuer}:{account}")}?secret={base32Secret}&issuer={System.Uri.EscapeDataString(issuer)}&digits=6&period={Step}";
}

public static class RecoveryCodes
{
    const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static List<string> New(int n = 8) =>
        Enumerable.Range(0, n).Select(_ => $"{Pick(4)}-{Pick(4)}").ToList();

    static string Pick(int n) => new(Enumerable.Range(0, n).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    public static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));
    public static string Normalize(string code) => new string((code ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Uses up a matching code: returns the remaining stored hashes, or null if it didn't match.</summary>
    public static string? Use(string stored, string code)
    {
        var hashes = stored.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var h = Hash(code);
        if (!hashes.Remove(h)) return null;
        return string.Join(';', hashes);
    }

    public static int Left(string stored) => stored.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>AES-GCM with a key derived from the JWT signing key, so a database copy alone doesn't reveal secrets.</summary>
public sealed class SecretBox(TokenIssuer tokens)
{
    byte[] Key => SHA256.HashData([.. tokens.Key.Key, .. "kabuor-erp/totp"u8]);

    public string Seal(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The secret, or null when it was sealed with a different server key (or is empty).</summary>
    public string? TryOpen(string sealedText)
    {
        if (string.IsNullOrEmpty(sealedText)) return null;
        try { return Open(sealedText); }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    public string Open(string sealedText)
    {
        var all = Convert.FromBase64String(sealedText);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(all[..12], all[28..], all[12..28], plain);
        return Encoding.UTF8.GetString(plain);
    }
}

static class Base32
{
    const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(A[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(A[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string s)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s.ToUpperInvariant().Where(c => c != '=' && c != ' '))
        {
            var v = A.IndexOf(c);
            if (v < 0) throw new FormatException("Not base32.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
