using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Infrastructure.Media;

namespace CalloraVoipSdk.Hosting;

/// <summary>
/// Factory for the SDK's built-in AES-256-GCM recording encryption. It hands back an
/// <see cref="IRecordingEncryptionProvider"/> ready to assign to
/// <see cref="RecordingOptions.EncryptionProvider"/>, so a consumer can encrypt finalized recording files
/// with the shipped reference implementation without writing their own crypto.
/// </summary>
/// <remarks>
/// The concrete provider (an AES-GCM-HKDF STREAM construction, constant memory regardless of file size) is an
/// internal Infrastructure implementation detail; this Client-layer facade is the public seam that builds it —
/// the same pattern the SDK uses to expose the built-in TURN/STUN servers
/// (<see cref="ITurnServerHost"/>, <see cref="IStunServerHost"/>). Keep the returned provider for the whole
/// recording session and dispose it (via <see cref="RecordingOptions"/> ownership or directly) so its key is
/// zeroed from memory.
/// <example>
/// <code>
/// using var provider = RecordingEncryption.FromPassphrase("correct horse battery staple", salt);
/// var options = new RecordingOptions { EncryptionProvider = provider };
/// </code>
/// </example>
/// </remarks>
public static class RecordingEncryption
{
    /// <summary>
    /// Creates a provider from a raw 32-byte AES-256 key.
    /// </summary>
    /// <param name="key">The AES-256 key; must be exactly 32 bytes.</param>
    /// <returns>An <see cref="IRecordingEncryptionProvider"/> that also implements <see cref="IDisposable"/>.</returns>
    /// <exception cref="ArgumentException">The key is not exactly 32 bytes long.</exception>
    public static IRecordingEncryptionProvider FromKey(ReadOnlySpan<byte> key)
        => new AesGcmRecordingEncryptionProvider(key);

    /// <summary>
    /// Creates a provider by deriving a 32-byte key from a passphrase and salt with PBKDF2-SHA256.
    /// </summary>
    /// <param name="passphrase">The passphrase to derive the key from; must not be null or whitespace.</param>
    /// <param name="salt">The PBKDF2 salt; must be at least 8 bytes. Store it alongside the recording so the
    /// same key can be re-derived for decryption.</param>
    /// <param name="iterations">PBKDF2 iteration count; must be at least 10,000. Defaults to 100,000.</param>
    /// <returns>An <see cref="IRecordingEncryptionProvider"/> that also implements <see cref="IDisposable"/>.</returns>
    /// <exception cref="ArgumentException">The passphrase is null/whitespace or the salt is shorter than 8 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="iterations"/> is below 10,000.</exception>
    public static IRecordingEncryptionProvider FromPassphrase(
        string passphrase,
        ReadOnlySpan<byte> salt,
        int iterations = 100_000)
        => AesGcmRecordingEncryptionProvider.FromPassphrase(passphrase, salt, iterations);
}
