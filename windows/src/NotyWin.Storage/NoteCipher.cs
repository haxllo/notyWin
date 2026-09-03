using System.Security.Cryptography;
using System.Text;

namespace NotyWin.Storage;

/// <summary>
/// AES-GCM note-body encryption. The macOS app stores a raw 32-byte key file
/// with 0o600 permissions next to the database. On Windows the same key file
/// would be readable by anything running as the user; we improve on the
/// original by wrapping the key with DPAPI so a raw 32-byte key is never
/// committed to disk in plaintext.
///
/// Layout on disk:
///   <c>%LocalAppData%\Noty\note.key.dpapi</c> — DPAPI-wrapped 32-byte AES key.
/// </summary>
public static class NoteCipher
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    /// <summary>Loads (or creates) the AES key, wrapped in DPAPI.</summary>
    public static byte[] LoadOrCreateKey(string wrappedKeyPath)
    {
        if (File.Exists(wrappedKeyPath))
        {
            var wrapped = File.ReadAllBytes(wrappedKeyPath);
            var key = Unprotect(wrapped);
            if (key.Length == KeyBytes) return key;
        }

        var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
        var wrapped2 = Protect(fresh);
        Directory.CreateDirectory(Path.GetDirectoryName(wrappedKeyPath)!);
        File.WriteAllBytes(wrappedKeyPath, wrapped2);
        return fresh;
    }

    public static byte[] Seal(string plain, byte[] key)
    {
        if (plain.Length == 0) return Array.Empty<byte>();
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        // Combined format matches macOS: nonce || cipher || tag.
        var combined = new byte[NonceBytes + cipher.Length + TagBytes];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceBytes);
        Buffer.BlockCopy(cipher, 0, combined, NonceBytes, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceBytes + cipher.Length, TagBytes);
        return combined;
    }

    public static string Open(byte[] combined, byte[] key)
    {
        if (combined.Length == 0) return "";
        if (combined.Length < NonceBytes + TagBytes) return "";

        var nonce = new byte[NonceBytes];
        Buffer.BlockCopy(combined, 0, nonce, 0, NonceBytes);
        var cipherLen = combined.Length - NonceBytes - TagBytes;
        var cipher = new byte[cipherLen];
        Buffer.BlockCopy(combined, NonceBytes, cipher, 0, cipherLen);
        var tag = new byte[TagBytes];
        Buffer.BlockCopy(combined, NonceBytes + cipherLen, tag, 0, TagBytes);

        try
        {
            var plain = new byte[cipherLen];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    // DPAPI is Windows-only. Storage assembly is net10.0-windows; this is safe.
    private static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
}