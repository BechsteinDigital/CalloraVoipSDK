using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CalloraVoipSdk.Core.Infrastructure.Media;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behavioral coverage for the streaming VREC2 AES-256-GCM recording encryption format:
/// round-trip fidelity, tamper detection, truncation and reordering protection and
/// passphrase key determinism.
/// </summary>
public sealed class AesGcmRecordingEncryptionProviderTests : IDisposable
{
    private const int Header = 5 + 8 + 4;              // magic + noncePrefix + chunkSize
    private const int FrameOverhead = 4 + 16;          // length prefix + tag
    private const int FullFrameSize = FrameOverhead + AesGcmRecordingEncryptionProvider.ChunkSize;

    private static readonly byte[] Key = CreateKey();

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "vrec2-tests-" + Guid.NewGuid().ToString("N"));

    public AesGcmRecordingEncryptionProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a leaked temp dir must not fail the suite.
        }
    }

    [Theory]
    [InlineData(0)]                                                       // empty
    [InlineData(100)]                                                     // < 1 chunk
    [InlineData(AesGcmRecordingEncryptionProvider.ChunkSize)]            // exactly 1 chunk
    [InlineData(AesGcmRecordingEncryptionProvider.ChunkSize + 1)]        // 1 chunk + 1 byte
    [InlineData(2 * AesGcmRecordingEncryptionProvider.ChunkSize + 123)]  // multiple chunks + partial
    public async Task RoundTrip_RecoversExactOriginalBytes(int size)
    {
        var original = RandomNumberGenerator.GetBytes(size);
        var plainIn = await WriteTempAsync("in.raw", original);
        var encrypted = Path.Combine(_dir, "cipher.enc");
        var plainOut = Path.Combine(_dir, "out.raw");

        var provider = new AesGcmRecordingEncryptionProvider(Key);
        await provider.EncryptFileAsync(plainIn, encrypted, CancellationToken.None);
        await provider.DecryptFileAsync(encrypted, plainOut, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(plainOut));
    }

    [Fact]
    public async Task Decrypt_FlippedCiphertextByte_ThrowsCryptographicException()
    {
        var encrypted = await EncryptRandomAsync(3 * AesGcmRecordingEncryptionProvider.ChunkSize);
        var bytes = await File.ReadAllBytesAsync(encrypted);

        // A byte inside the first chunk's ciphertext.
        var target = Header + FrameOverhead + 10;
        bytes[target] ^= 0xFF;
        await File.WriteAllBytesAsync(encrypted, bytes);

        await AssertDecryptThrowsAsync<CryptographicException>(encrypted);
    }

    [Fact]
    public async Task Decrypt_DroppedFinalChunk_ThrowsCryptographicException()
    {
        // 3 full chunks => 3 equally sized frames; drop the last one.
        var encrypted = await EncryptRandomAsync(3 * AesGcmRecordingEncryptionProvider.ChunkSize);
        var bytes = await File.ReadAllBytesAsync(encrypted);

        var truncated = bytes.AsSpan(0, bytes.Length - FullFrameSize).ToArray();
        await File.WriteAllBytesAsync(encrypted, truncated);

        // The former penultimate chunk was authenticated as non-final; it now looks final.
        await AssertDecryptThrowsAsync<CryptographicException>(encrypted);
    }

    [Fact]
    public async Task Decrypt_SwappedChunks_ThrowsCryptographicException()
    {
        var encrypted = await EncryptRandomAsync(3 * AesGcmRecordingEncryptionProvider.ChunkSize);
        var bytes = await File.ReadAllBytesAsync(encrypted);

        var frame0 = Header;
        var frame1 = Header + FullFrameSize;
        var tmp = bytes.AsSpan(frame0, FullFrameSize).ToArray();
        bytes.AsSpan(frame1, FullFrameSize).CopyTo(bytes.AsSpan(frame0, FullFrameSize));
        tmp.CopyTo(bytes.AsSpan(frame1, FullFrameSize));
        await File.WriteAllBytesAsync(encrypted, bytes);

        // Chunk indices bound into the AAD no longer match their position.
        await AssertDecryptThrowsAsync<CryptographicException>(encrypted);
    }

    [Fact]
    public async Task FromPassphrase_DerivesSameKey_AcrossInstances()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var original = RandomNumberGenerator.GetBytes(4096);
        var plainIn = await WriteTempAsync("pp.raw", original);
        var encrypted = Path.Combine(_dir, "pp.enc");
        var plainOut = Path.Combine(_dir, "pp.out");

        var writer = AesGcmRecordingEncryptionProvider.FromPassphrase("correct horse battery staple", salt);
        var reader = AesGcmRecordingEncryptionProvider.FromPassphrase("correct horse battery staple", salt);

        await writer.EncryptFileAsync(plainIn, encrypted, CancellationToken.None);
        await reader.DecryptFileAsync(encrypted, plainOut, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(plainOut));
    }

    [Fact]
    public async Task Decrypt_UnknownMagic_ThrowsInvalidData()
    {
        var bogus = Path.Combine(_dir, "bogus.enc");
        await File.WriteAllBytesAsync(bogus, RandomNumberGenerator.GetBytes(64));

        var provider = new AesGcmRecordingEncryptionProvider(Key);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await provider.DecryptFileAsync(bogus, Path.Combine(_dir, "bogus.out"), CancellationToken.None));
    }

    private async Task AssertDecryptThrowsAsync<TException>(string encrypted)
        where TException : Exception
    {
        var provider = new AesGcmRecordingEncryptionProvider(Key);
        await Assert.ThrowsAnyAsync<TException>(async () =>
            await provider.DecryptFileAsync(encrypted, Path.Combine(_dir, "tampered.out"), CancellationToken.None));
    }

    private async Task<string> EncryptRandomAsync(int size)
    {
        var plainIn = await WriteTempAsync("src.raw", RandomNumberGenerator.GetBytes(size));
        var encrypted = Path.Combine(_dir, "src.enc");
        var provider = new AesGcmRecordingEncryptionProvider(Key);
        await provider.EncryptFileAsync(plainIn, encrypted, CancellationToken.None);
        return encrypted;
    }

    private async Task<string> WriteTempAsync(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static byte[] CreateKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(i + 1);
        return key;
    }
}
