using System.Security.Cryptography;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Infrastructure.Media;
using CalloraVoipSdk.Hosting;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Covers the public <see cref="RecordingEncryption"/> factory (the 4.7.0 consumer-facing seam that rebuilds
/// the built-in AES-GCM recording encryption after the impl was internal-ised): both build paths produce a
/// working provider assignable to <see cref="RecordingOptions.EncryptionProvider"/>, and passphrase derivation
/// is deterministic (a provider built independently from the same passphrase+salt decrypts what the first
/// encrypted). The factory returns the public <see cref="IRecordingEncryptionProvider"/> — which only exposes
/// encryption — so the roundtrip decrypts through the internal impl (visible here via InternalsVisibleTo).
/// </summary>
public sealed class RecordingEncryptionFactoryTests
{
    [Fact]
    public async Task FromKey_produces_provider_that_roundtrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        IRecordingEncryptionProvider provider = RecordingEncryption.FromKey(key);
        try
        {
            Assert.Equal("enc", provider.OutputFileExtension);
            await AssertRoundtrips(provider, provider);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task FromPassphrase_is_deterministic_across_independent_providers()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        const string passphrase = "correct horse battery staple";

        // Two independently constructed providers from the same passphrase+salt derive the same key,
        // so one can decrypt what the other encrypted.
        IRecordingEncryptionProvider encryptor = RecordingEncryption.FromPassphrase(passphrase, salt);
        IRecordingEncryptionProvider decryptor = RecordingEncryption.FromPassphrase(passphrase, salt);
        try
        {
            await AssertRoundtrips(encryptor, decryptor);
        }
        finally
        {
            (encryptor as IDisposable)?.Dispose();
            (decryptor as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void FromKey_rejects_wrong_key_length()
        => Assert.Throws<ArgumentException>(() => RecordingEncryption.FromKey(new byte[16]));

    [Fact]
    public void FromPassphrase_rejects_short_salt()
        => Assert.Throws<ArgumentException>(() => RecordingEncryption.FromPassphrase("pw", new byte[4]));

    [Fact]
    public void FromPassphrase_rejects_low_iterations()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => RecordingEncryption.FromPassphrase("pw", new byte[16], iterations: 1_000));

    private static async Task AssertRoundtrips(
        IRecordingEncryptionProvider encryptor,
        IRecordingEncryptionProvider decryptor)
    {
        var plaintext = RandomNumberGenerator.GetBytes(5_000);
        var dir = Path.Combine(Path.GetTempPath(), "callora-rec-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var inputPath = Path.Combine(dir, "in.pcm");
        var encPath = Path.Combine(dir, "in." + encryptor.OutputFileExtension);
        var decPath = Path.Combine(dir, "out.pcm");
        try
        {
            await File.WriteAllBytesAsync(inputPath, plaintext);
            await encryptor.EncryptFileAsync(inputPath, encPath);

            // The public port only encrypts; decrypt via the built-in impl to prove the ciphertext is
            // well-formed and the derived key matches. Cast is safe — the factory builds this exact type.
            await ((AesGcmRecordingEncryptionProvider)decryptor).DecryptFileAsync(encPath, decPath);

            Assert.Equal(plaintext, await File.ReadAllBytesAsync(decPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
