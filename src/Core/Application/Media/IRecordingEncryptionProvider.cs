namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Optional provider that encrypts finalized recording files.
/// </summary>
public interface IRecordingEncryptionProvider
{
    /// <summary>
    /// File extension (without dot) for encrypted output files.
    /// </summary>
    string OutputFileExtension { get; }

    /// <summary>
    /// Encrypts one recorded input file into <paramref name="encryptedOutputPath"/>.
    /// </summary>
    ValueTask EncryptFileAsync(
        string inputFilePath,
        string encryptedOutputPath,
        CancellationToken ct = default);

    /// <summary>
    /// Decrypts a file previously produced by
    /// <see cref="EncryptFileAsync(string, string, CancellationToken)"/> back into plaintext,
    /// writing the recovered content to <paramref name="decryptedOutputPath"/>.
    /// </summary>
    /// <param name="encryptedInputPath">Path to the encrypted input file.</param>
    /// <param name="decryptedOutputPath">Destination path for the recovered plaintext.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the plaintext has been fully written and flushed.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Authentication of the encrypted content failed: the data was tampered with, reordered,
    /// truncated or extended, or the wrong key was used.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// The encrypted file has an unrecognized or malformed container format.
    /// </exception>
    ValueTask DecryptFileAsync(
        string encryptedInputPath,
        string decryptedOutputPath,
        CancellationToken ct = default);
}
