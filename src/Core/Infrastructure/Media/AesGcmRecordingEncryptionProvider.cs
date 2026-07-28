using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CalloraVoipSdk.Core.Application.Media;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

/// <summary>
/// AES-256-GCM reference implementation for recording file encryption.
/// </summary>
/// <remarks>
/// Encrypts and decrypts in fixed-size chunks (an AES-GCM-HKDF STREAM construction), so a recording of
/// any length is processed with constant memory (about one chunk) instead of loading the whole file. Each
/// file draws a random salt and nonce prefix; a per-file key is derived with HKDF-SHA256 so the same
/// long-term key never reuses an AES-GCM (key, nonce) pair across files. Within a file each chunk gets a
/// distinct nonce (prefix + 32-bit chunk index + a last-chunk flag), which also binds chunk order and makes
/// truncation or reordering fail the GCM authentication. The long-term key is held in memory for the
/// provider's lifetime; call <see cref="Dispose"/> to zero it.
/// </remarks>
public sealed class AesGcmRecordingEncryptionProvider : IRecordingEncryptionProvider, IDisposable
{
    private const string MagicV2 = "VREC2"; // chunked/streaming (this implementation)
    private const string MagicV1 = "VREC1"; // legacy whole-file blob, still decryptable
    private const int MagicSize = 5;
    private const int SaltSize = 16;
    private const int NoncePrefixSize = 7;
    private const int NonceSize = 12; // 7-byte prefix + 4-byte chunk index + 1-byte last-chunk flag
    private const int TagSize = 16;
    private const int ChunkSize = 64 * 1024; // plaintext bytes per chunk

    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("CalloraVoipSdk.RecordingEncryption.VREC2");

    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>
    /// Creates an encryption provider from a raw 32-byte AES key.
    /// </summary>
    public AesGcmRecordingEncryptionProvider(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("AES-256-GCM key must be exactly 32 bytes.", nameof(key));

        _key = key.ToArray();
    }

    /// <summary>
    /// Creates an encryption provider by deriving a 32-byte key from passphrase+salt (PBKDF2-SHA256).
    /// </summary>
    public static AesGcmRecordingEncryptionProvider FromPassphrase(
        string passphrase,
        ReadOnlySpan<byte> salt,
        int iterations = 100_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        if (salt.Length < 8)
            throw new ArgumentException("Salt must be at least 8 bytes.", nameof(salt));
        if (iterations < 10_000)
            throw new ArgumentOutOfRangeException(nameof(iterations), "PBKDF2 iterations must be >= 10,000.");

        var key = Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt.ToArray(),
            iterations,
            HashAlgorithmName.SHA256,
            32);
        return new AesGcmRecordingEncryptionProvider(key);
    }

    /// <inheritdoc />
    public string OutputFileExtension => "enc";

    /// <inheritdoc />
    public async ValueTask EncryptFileAsync(
        string inputFilePath,
        string encryptedOutputPath,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedOutputPath);
        EnsureDirectory(encryptedOutputPath);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt, HkdfInfo);
        try
        {
            await using var input = OpenRead(inputFilePath);
            await using var output = OpenWrite(encryptedOutputPath);

            await output.WriteAsync(Encoding.ASCII.GetBytes(MagicV2), ct).ConfigureAwait(false);
            await output.WriteAsync(salt, ct).ConfigureAwait(false);
            await output.WriteAsync(noncePrefix, ct).ConfigureAwait(false);

            var plaintext = new byte[ChunkSize];
            var chunk = new byte[ChunkSize + TagSize]; // ciphertext + trailing tag
            var nonce = new byte[NonceSize];
            noncePrefix.CopyTo(nonce.AsSpan(0, NoncePrefixSize));

            using var aes = new AesGcm(derivedKey, TagSize);
            uint counter = 0;
            bool isLast;
            do
            {
                var read = await input
                    .ReadAtLeastAsync(plaintext, ChunkSize, throwOnEndOfStream: false, ct)
                    .ConfigureAwait(false);
                // On a seekable stream, the block that leaves us at EOF is the last one — even an empty
                // input produces exactly one (empty) last chunk so decryption is symmetric.
                isLast = input.Position >= input.Length;
                WriteNonce(nonce, counter, isLast);

                aes.Encrypt(
                    nonce,
                    plaintext.AsSpan(0, read),
                    chunk.AsSpan(0, read),
                    chunk.AsSpan(read, TagSize),
                    associatedData: null);
                await output.WriteAsync(chunk.AsMemory(0, read + TagSize), ct).ConfigureAwait(false);

                counter = checked(counter + 1);
            }
            while (!isLast);

            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Decrypts a file produced by <see cref="EncryptFileAsync"/> back to plaintext, streaming chunk by
    /// chunk. Also reads the legacy whole-file <c>VREC1</c> format. Throws
    /// <see cref="CryptographicException"/> if the file was tampered with, truncated or reordered (the
    /// per-chunk authentication tag fails), and <see cref="InvalidOperationException"/> for an unknown
    /// header.
    /// </summary>
    public async ValueTask DecryptFileAsync(
        string encryptedFilePath,
        string decryptedOutputPath,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(decryptedOutputPath);
        EnsureDirectory(decryptedOutputPath);

        await using var input = OpenRead(encryptedFilePath);
        await using var output = OpenWrite(decryptedOutputPath);

        var magic = new byte[MagicSize];
        await input.ReadExactlyAsync(magic, ct).ConfigureAwait(false);
        var magicText = Encoding.ASCII.GetString(magic);

        if (magicText == MagicV1)
        {
            await DecryptLegacyV1Async(input, output, ct).ConfigureAwait(false);
            return;
        }

        if (magicText != MagicV2)
            throw new InvalidOperationException($"Unrecognized recording encryption format header '{magicText}'.");

        var salt = new byte[SaltSize];
        await input.ReadExactlyAsync(salt, ct).ConfigureAwait(false);
        var noncePrefix = new byte[NoncePrefixSize];
        await input.ReadExactlyAsync(noncePrefix, ct).ConfigureAwait(false);

        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt, HkdfInfo);
        try
        {
            var nonce = new byte[NonceSize];
            noncePrefix.CopyTo(nonce.AsSpan(0, NoncePrefixSize));
            var chunk = new byte[ChunkSize + TagSize];
            var plaintext = new byte[ChunkSize];

            using var aes = new AesGcm(derivedKey, TagSize);
            uint counter = 0;
            while (input.Position < input.Length)
            {
                var toRead = (int)Math.Min(ChunkSize + TagSize, input.Length - input.Position);
                if (toRead < TagSize)
                    throw new InvalidOperationException(
                        "Encrypted recording is truncated: a chunk is shorter than the authentication tag.");

                await input.ReadExactlyAsync(chunk.AsMemory(0, toRead), ct).ConfigureAwait(false);
                var isLast = input.Position >= input.Length;
                WriteNonce(nonce, counter, isLast);

                var ciphertextLen = toRead - TagSize;
                // Throws CryptographicException when the tag does not verify — tamper, or a truncation/
                // reorder that shifts a chunk's index or last-flag away from what it was sealed with.
                aes.Decrypt(
                    nonce,
                    chunk.AsSpan(0, ciphertextLen),
                    chunk.AsSpan(ciphertextLen, TagSize),
                    plaintext.AsSpan(0, ciphertextLen),
                    associatedData: null);
                await output.WriteAsync(plaintext.AsMemory(0, ciphertextLen), ct).ConfigureAwait(false);

                counter = checked(counter + 1);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    // VREC1 (legacy, whole-file): nonce(12) + tag(16) + ciphertext(rest), sealed with the raw key (no HKDF).
    // Loads the ciphertext fully — only for files produced before the streaming format; kept for compatibility.
    private async ValueTask DecryptLegacyV1Async(Stream input, Stream output, CancellationToken ct)
    {
        var nonce = new byte[NonceSize];
        await input.ReadExactlyAsync(nonce, ct).ConfigureAwait(false);
        var tag = new byte[TagSize];
        await input.ReadExactlyAsync(tag, ct).ConfigureAwait(false);
        var ciphertext = new byte[input.Length - input.Position];
        await input.ReadExactlyAsync(ciphertext, ct).ConfigureAwait(false);

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, ciphertext, associatedData: null); // decrypt in place
        await output.WriteAsync(ciphertext, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteNonce(byte[] nonce, uint chunkIndex, bool isLast)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize, 4), chunkIndex);
        nonce[NoncePrefixSize + 4] = isLast ? (byte)1 : (byte)0;
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        bufferSize: ChunkSize, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenWrite(string path) => new(
        path, FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: ChunkSize, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Zeroes the AES key held in memory. After disposal the provider can no longer encrypt or decrypt.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}
