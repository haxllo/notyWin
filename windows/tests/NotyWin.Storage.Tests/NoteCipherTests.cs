using NotyWin.Storage;
using Xunit;

namespace NotyWin.Storage.Tests;

public class NoteCipherTests
{
    [Fact]
    public void Seal_ThenOpen_RoundTripsUnicode()
    {
        var key = MakeKey();
        var plain = "Hello \u4e16\u754c — café ⌘";
        var sealedBytes = NoteCipher.Seal(plain, key);
        var opened = NoteCipher.Open(sealedBytes, key);
        Assert.Equal(plain, opened);
    }

    [Fact]
    public void Seal_Empty_ReturnsEmpty()
    {
        var key = MakeKey();
        Assert.Empty(NoteCipher.Seal("", key));
        Assert.Equal("", NoteCipher.Open(Array.Empty<byte>(), key));
    }

    [Fact]
    public void Open_WrongKey_ReturnsEmpty()
    {
        var keyA = MakeKey();
        var keyB = MakeKey();
        var sealedBytes = NoteCipher.Seal("secret", keyA);
        Assert.Equal("", NoteCipher.Open(sealedBytes, keyB));
    }

    [Fact]
    public void Open_TruncatedCipher_ReturnsEmpty()
    {
        var key = MakeKey();
        var sealedBytes = NoteCipher.Seal("hello", key);
        // Drop the last 4 bytes — guaranteed to fail GCM tag check.
        var truncated = sealedBytes.Take(sealedBytes.Length - 4).ToArray();
        Assert.Equal("", NoteCipher.Open(truncated, key));
    }

    [Fact]
    public void Seal_ProducesDifferentCiphertextEachTime()
    {
        // AES-GCM with a fresh random nonce: same plaintext yields different ciphertext.
        var key = MakeKey();
        var a = NoteCipher.Seal("same", key);
        var b = NoteCipher.Seal("same", key);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LoadOrCreateKey_PersistsAcrossInstances()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "note.key.dpapi");
        try
        {
            var k1 = NoteCipher.LoadOrCreateKey(path);
            Assert.Equal(32, k1.Length);
            var k2 = NoteCipher.LoadOrCreateKey(path);
            Assert.Equal(k1, k2);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadOrCreateKey_ReuseKeyRoundTripsSealed()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "note.key.dpapi");
        try
        {
            var k1 = NoteCipher.LoadOrCreateKey(path);
            var sealedBytes = NoteCipher.Seal("hello", k1);
            var k2 = NoteCipher.LoadOrCreateKey(path);
            Assert.Equal("hello", NoteCipher.Open(sealedBytes, k2));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] MakeKey()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "note.key.dpapi");
        var key = NoteCipher.LoadOrCreateKey(path);
        // Each test gets a fresh key — clean up.
        try { Directory.Delete(dir, recursive: true); } catch { }
        return key;
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "NotyWinTests-" + Guid.NewGuid().ToString("N"));
}
