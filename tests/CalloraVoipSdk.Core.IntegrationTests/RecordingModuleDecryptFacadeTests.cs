using System.Security.Cryptography;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Infrastructure.Media;
using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Facade-level coverage for <see cref="IRecordingModule.DecryptRecordingAsync"/>.
/// The concrete <c>CoreRecordingModule</c> is a thin delegation onto the encryption
/// provider, so these tests verify that the public recording facade exposes a working,
/// symmetric decryption path (round-trip fidelity, tamper detection and input validation)
/// without requiring an active recording session.
/// </summary>
public sealed class RecordingModuleDecryptFacadeTests : IDisposable
{
    private const int Header = 5 + 8 + 4;      // magic + noncePrefix + chunkSize
    private const int FrameOverhead = 4 + 16;  // length prefix + tag

    private static readonly byte[] Key = CreateKey();

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "vrec2-facade-tests-" + Guid.NewGuid().ToString("N"));

    public RecordingModuleDecryptFacadeTests() => Directory.CreateDirectory(_dir);

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

    private static IRecordingModule CreateFacade() =>
        ModuleAdapters.CreateRecording(new MediaManager());

    [Fact]
    public async Task DecryptRecordingAsync_AfterProviderEncrypt_RecoversExactBytes()
    {
        var original = RandomNumberGenerator.GetBytes(2 * AesGcmRecordingEncryptionProvider.ChunkSize + 321);
        var plainIn = Path.Combine(_dir, "in.raw");
        var encrypted = Path.Combine(_dir, "cipher.enc");
        var plainOut = Path.Combine(_dir, "out.raw");
        await File.WriteAllBytesAsync(plainIn, original);

        var provider = new AesGcmRecordingEncryptionProvider(Key);
        await provider.EncryptFileAsync(plainIn, encrypted, CancellationToken.None);

        var facade = CreateFacade();
        await facade.DecryptRecordingAsync(encrypted, plainOut, provider, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(plainOut));
    }

    [Fact]
    public async Task DecryptRecordingAsync_TamperedCiphertext_Throws()
    {
        var plainIn = Path.Combine(_dir, "in.raw");
        var encrypted = Path.Combine(_dir, "cipher.enc");
        await File.WriteAllBytesAsync(plainIn, RandomNumberGenerator.GetBytes(4096));

        var provider = new AesGcmRecordingEncryptionProvider(Key);
        await provider.EncryptFileAsync(plainIn, encrypted, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(encrypted);
        bytes[Header + FrameOverhead + 5] ^= 0xFF; // flip a ciphertext byte in the first chunk
        await File.WriteAllBytesAsync(encrypted, bytes);

        var facade = CreateFacade();
        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await facade.DecryptRecordingAsync(
                encrypted, Path.Combine(_dir, "tampered.out"), provider, CancellationToken.None));
    }

    [Fact]
    public async Task DecryptRecordingAsync_NullProvider_ThrowsArgumentNull()
    {
        var facade = CreateFacade();
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await facade.DecryptRecordingAsync("in.enc", "out.raw", null!, CancellationToken.None));
    }

    [Fact]
    public async Task DecryptRecordingAsync_BlankPath_ThrowsArgument()
    {
        var facade = CreateFacade();
        var provider = new AesGcmRecordingEncryptionProvider(Key);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await facade.DecryptRecordingAsync(" ", "out.raw", provider, CancellationToken.None));
    }

    private static byte[] CreateKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(i + 1);
        return key;
    }
}
