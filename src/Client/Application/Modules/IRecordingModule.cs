using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Modules;

/// <summary>
/// SDK facade for recording features.
/// </summary>
public interface IRecordingModule
{
    /// <summary>True when this module can be used in the current runtime context.</summary>
    bool IsAvailable { get; }

    /// <summary>Active recording sessions.</summary>
    IReadOnlyCollection<IRecordingSession> Active { get; }

    Task<IRecordingSession> StartCallAsync(ICall call, RecordingOptions? options = null, CancellationToken ct = default);

    Task<IRecordingSession> StartMixedBusAsync(IMixedMediaBus bus, RecordingOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Decrypts a previously encrypted recording file back into plaintext.
    /// </summary>
    /// <remarks>
    /// This is a standalone file operation and does not require an active recording session;
    /// it can be invoked at any time regardless of <see cref="Active"/>.
    /// </remarks>
    /// <param name="encryptedInputPath">Path to the encrypted recording file.</param>
    /// <param name="decryptedOutputPath">Destination path for the recovered plaintext.</param>
    /// <param name="provider">
    /// Encryption provider matching the one used to produce the encrypted file
    /// (same algorithm and key material).
    /// </param>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the plaintext has been fully written.</returns>
    /// <exception cref="System.ArgumentException">A supplied path is null, empty or whitespace.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="provider"/> is null.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Authentication failed: wrong key, or the encrypted content was tampered with,
    /// reordered, truncated or extended.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// The encrypted file has an unrecognized or malformed container format.
    /// </exception>
    Task DecryptRecordingAsync(
        string encryptedInputPath,
        string decryptedOutputPath,
        IRecordingEncryptionProvider provider,
        CancellationToken ct = default);
}
