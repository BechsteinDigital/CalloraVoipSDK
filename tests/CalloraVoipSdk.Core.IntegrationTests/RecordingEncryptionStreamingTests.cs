using System.Security.Cryptography;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Media;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Streaming AES-GCM-HKDF recording encryption (VREC2): constant-memory chunked roundtrip,
/// tamper/truncation rejection, legacy VREC1 compatibility and per-file nonce independence.
/// </summary>
public sealed class RecordingEncryptionStreamingTests
{
    private const int ChunkSize = 64 * 1024;

    [Theory]
    [InlineData(0)]              // empty recording → single empty last chunk
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(ChunkSize - 1)]  // partial single chunk
    [InlineData(ChunkSize)]      // exact chunk boundary
    [InlineData(ChunkSize + 1)]  // spills into a second (last) chunk
    [InlineData(3 * ChunkSize + 123)] // several full chunks + partial tail
    public async Task Roundtrips_plaintext_of_any_length(int size)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = RandomNumberGenerator.GetBytes(size);
        await WithTempFiles(async (inputPath, encPath, decPath) =>
        {
            await File.WriteAllBytesAsync(inputPath, plaintext);
            using var provider = new AesGcmRecordingEncryptionProvider(key);
            await provider.EncryptFileAsync(inputPath, encPath);
            await provider.DecryptFileAsync(encPath, decPath);

            var decrypted = await File.ReadAllBytesAsync(decPath);
            Assert.Equal(plaintext, decrypted);
        });
    }

    [Fact]
    public async Task Same_plaintext_produces_distinct_ciphertext_across_files()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = RandomNumberGenerator.GetBytes(10_000);
        await WithTempFiles(async (inputPath, encPath1, _) =>
        {
            var encPath2 = encPath1 + ".2";
            try
            {
                await File.WriteAllBytesAsync(inputPath, plaintext);
                using var provider = new AesGcmRecordingEncryptionProvider(key);
                await provider.EncryptFileAsync(inputPath, encPath1);
                await provider.EncryptFileAsync(inputPath, encPath2);

                var enc1 = await File.ReadAllBytesAsync(encPath1);
                var enc2 = await File.ReadAllBytesAsync(encPath2);
                // Random per-file salt + nonce prefix → the two containers must differ despite identical input.
                Assert.NotEqual(enc1, enc2);
            }
            finally
            {
                File.Delete(encPath2);
            }
        });
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = RandomNumberGenerator.GetBytes(5_000);
        await WithTempFiles(async (inputPath, encPath, decPath) =>
        {
            await File.WriteAllBytesAsync(inputPath, plaintext);
            using var provider = new AesGcmRecordingEncryptionProvider(key);
            await provider.EncryptFileAsync(inputPath, encPath);

            var enc = await File.ReadAllBytesAsync(encPath);
            // Flip a byte inside the ciphertext body (past the 28-byte header).
            enc[40] ^= 0xFF;
            await File.WriteAllBytesAsync(encPath, enc);

            await Assert.ThrowsAnyAsync<CryptographicException>(
                async () => await provider.DecryptFileAsync(encPath, decPath));
        });
    }

    [Fact]
    public async Task Truncated_ciphertext_is_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        // Two full chunks so we can drop the final chunk's tail without emptying the file.
        var plaintext = RandomNumberGenerator.GetBytes(ChunkSize + 5_000);
        await WithTempFiles(async (inputPath, encPath, decPath) =>
        {
            await File.WriteAllBytesAsync(inputPath, plaintext);
            using var provider = new AesGcmRecordingEncryptionProvider(key);
            await provider.EncryptFileAsync(inputPath, encPath);

            var enc = await File.ReadAllBytesAsync(encPath);
            // Chop the last 100 bytes: the final chunk now carries the wrong last-flag/index and fails auth.
            await File.WriteAllBytesAsync(encPath, enc[..(enc.Length - 100)]);

            await Assert.ThrowsAnyAsync<CryptographicException>(
                async () => await provider.DecryptFileAsync(encPath, decPath));
        });
    }

    [Fact]
    public async Task Unknown_header_is_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await WithTempFiles(async (_, encPath, decPath) =>
        {
            await File.WriteAllBytesAsync(encPath, Encoding.ASCII.GetBytes("XXXXX-not-a-recording"));
            using var provider = new AesGcmRecordingEncryptionProvider(key);
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await provider.DecryptFileAsync(encPath, decPath));
        });
    }

    [Fact]
    public async Task Legacy_VREC1_blob_still_decrypts()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = RandomNumberGenerator.GetBytes(12_345);
        await WithTempFiles(async (_, encPath, decPath) =>
        {
            // Hand-build the legacy whole-file container: "VREC1"(5) + nonce(12) + tag(16) + ciphertext,
            // sealed with the raw key (no HKDF) — exactly what the pre-streaming provider wrote.
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData: null);

            using var blob = new MemoryStream();
            blob.Write(Encoding.ASCII.GetBytes("VREC1"));
            blob.Write(nonce);
            blob.Write(tag);
            blob.Write(ciphertext);
            await File.WriteAllBytesAsync(encPath, blob.ToArray());

            using var provider = new AesGcmRecordingEncryptionProvider(key);
            await provider.DecryptFileAsync(encPath, decPath);

            Assert.Equal(plaintext, await File.ReadAllBytesAsync(decPath));
        });
    }

    [Fact]
    public async Task Wrong_key_is_rejected()
    {
        var plaintext = RandomNumberGenerator.GetBytes(8_000);
        await WithTempFiles(async (inputPath, encPath, decPath) =>
        {
            await File.WriteAllBytesAsync(inputPath, plaintext);
            using (var writer = new AesGcmRecordingEncryptionProvider(RandomNumberGenerator.GetBytes(32)))
                await writer.EncryptFileAsync(inputPath, encPath);

            using var reader = new AesGcmRecordingEncryptionProvider(RandomNumberGenerator.GetBytes(32));
            await Assert.ThrowsAnyAsync<CryptographicException>(
                async () => await reader.DecryptFileAsync(encPath, decPath));
        });
    }

    [Fact]
    public async Task Decrypt_after_dispose_throws()
    {
        var provider = new AesGcmRecordingEncryptionProvider(RandomNumberGenerator.GetBytes(32));
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await provider.DecryptFileAsync("in.enc", "out.wav"));
    }

    private static async Task WithTempFiles(Func<string, string, string, Task> body)
    {
        var inputPath = Path.GetTempFileName();
        var encPath = inputPath + ".enc";
        var decPath = inputPath + ".dec";
        try
        {
            await body(inputPath, encPath, decPath);
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(encPath);
            File.Delete(decPath);
        }
    }
}
