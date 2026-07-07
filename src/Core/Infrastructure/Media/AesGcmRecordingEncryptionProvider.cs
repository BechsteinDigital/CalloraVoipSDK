using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Application.Media;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

/// <summary>
/// AES-256-GCM reference implementation for recording file encryption.
/// </summary>
/// <remarks>
/// <para>
/// Encryption and decryption are fully streaming: input and output are processed in
/// fixed-size chunks so that the resident memory stays constant regardless of file size.
/// This makes the provider suitable for multi-hour, multi-gigabyte recordings.
/// </para>
/// <para>
/// On-disk container format <c>VREC2</c> (all multi-byte integers big-endian):
/// </para>
/// <code>
/// Header:
///   magic        : 5 bytes ASCII "VREC2"
///   noncePrefix  : 8 bytes random (unique per file)
///   chunkSize    : 4 bytes uint32 (plaintext chunk size used by the writer)
/// Then, repeated per chunk (chunk index starts at 0, strictly increasing):
///   ciphertextLen: 4 bytes uint32
///   tag          : 16 bytes GCM authentication tag
///   ciphertext   : ciphertextLen bytes
/// </code>
/// <para>
/// Per-chunk AES-256-GCM nonce = <c>noncePrefix (8B) || chunkCounter (4B uint32)</c>.
/// The random 8-byte prefix combined with the monotonic counter guarantees a unique
/// nonce for every chunk under the same key, so no nonce is ever reused.
/// </para>
/// <para>
/// Per-chunk associated data (AAD) = <c>magic (5B) || chunkIndex (8B uint64) || isFinal (1B)</c>.
/// Binding the chunk index defeats reordering; binding the "final chunk" flag defeats
/// truncation (dropping the last chunk or appending extra chunks): in either case the GCM
/// verification of the affected chunk fails with a <see cref="CryptographicException"/>.
/// </para>
/// </remarks>
public sealed class AesGcmRecordingEncryptionProvider : IRecordingEncryptionProvider
{
    /// <summary>
    /// Plaintext chunk size in bytes used by the encryptor (64 KiB). The value is also
    /// persisted in the file header so decryption is independent of this constant.
    /// </summary>
    public const int ChunkSize = 64 * 1024;

    private static readonly byte[] MagicBytes = "VREC2"u8.ToArray();

    private const int MagicSize = 5;
    private const int NoncePrefixSize = 8;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int LengthPrefixSize = 4;
    private const int HeaderSize = MagicSize + NoncePrefixSize + LengthPrefixSize;
    private const int AadSize = MagicSize + sizeof(ulong) + 1;

    /// <summary>Upper bound accepted for the header chunk size on decryption (64 MiB).</summary>
    private const int MaxChunkSize = 64 * 1024 * 1024;

    private readonly byte[] _key;

    /// <summary>
    /// Creates an encryption provider from a raw 32-byte AES key.
    /// </summary>
    /// <param name="key">Exactly 32 bytes of AES-256 key material.</param>
    /// <exception cref="ArgumentException">The key is not exactly 32 bytes long.</exception>
    public AesGcmRecordingEncryptionProvider(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("AES-256-GCM key must be exactly 32 bytes.", nameof(key));

        _key = key.ToArray();
    }

    /// <summary>
    /// Creates an encryption provider by deriving a 32-byte key from passphrase+salt (PBKDF2-SHA256).
    /// </summary>
    /// <param name="passphrase">Non-empty passphrase.</param>
    /// <param name="salt">Salt of at least 8 bytes.</param>
    /// <param name="iterations">PBKDF2 iteration count (>= 10,000).</param>
    /// <returns>A provider whose key is deterministic for the same passphrase, salt and iteration count.</returns>
    /// <exception cref="ArgumentException">Passphrase is empty or salt is too short.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Iteration count is below 10,000.</exception>
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
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedOutputPath);

        EnsureDirectory(encryptedOutputPath);

        await using var input = OpenRead(inputFilePath);
        await using var output = OpenWrite(encryptedOutputPath);

        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);

        // Header: magic || noncePrefix || chunkSize.
        var header = new byte[HeaderSize];
        MagicBytes.AsSpan().CopyTo(header);
        noncePrefix.AsSpan().CopyTo(header.AsSpan(MagicSize));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(MagicSize + NoncePrefixSize), (uint)ChunkSize);
        await output.WriteAsync(header, ct).ConfigureAwait(false);

        var pool = ArrayPool<byte>.Shared;
        var curBuf = pool.Rent(ChunkSize);
        var nxtBuf = pool.Rent(ChunkSize);
        var cipherBuf = pool.Rent(ChunkSize);
        var tag = new byte[TagSize];
        var lengthPrefix = new byte[LengthPrefixSize];

        using var aes = new AesGcm(_key, TagSize);
        try
        {
            var curLen = await ReadChunkAsync(input, curBuf, ct).ConfigureAwait(false);
            long index = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // A further chunk can only exist if the current buffer was filled completely.
                var nxtLen = 0;
                var hasNext = false;
                if (curLen == ChunkSize)
                {
                    nxtLen = await ReadChunkAsync(input, nxtBuf, ct).ConfigureAwait(false);
                    hasNext = nxtLen > 0;
                }

                var isFinal = !hasNext;

                if (index > uint.MaxValue)
                    throw new InvalidOperationException("Recording exceeds the maximum supported chunk count.");

                EncryptChunk(
                    aes,
                    noncePrefix,
                    (uint)index,
                    index,
                    isFinal,
                    curBuf.AsSpan(0, curLen),
                    cipherBuf.AsSpan(0, curLen),
                    tag);

                BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)curLen);
                await output.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
                await output.WriteAsync(tag, ct).ConfigureAwait(false);
                await output.WriteAsync(cipherBuf.AsMemory(0, curLen), ct).ConfigureAwait(false);

                if (isFinal)
                    break;

                (curBuf, nxtBuf) = (nxtBuf, curBuf);
                curLen = nxtLen;
                index++;
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(curBuf.AsSpan(0, ChunkSize));
            CryptographicOperations.ZeroMemory(nxtBuf.AsSpan(0, ChunkSize));
            pool.Return(curBuf);
            pool.Return(nxtBuf);
            pool.Return(cipherBuf);
        }
    }

    /// <summary>
    /// Decrypts a <c>VREC2</c> file produced by <see cref="EncryptFileAsync"/> back into plaintext,
    /// verifying every chunk. Processing is streaming with constant memory use.
    /// </summary>
    /// <param name="encryptedInputPath">Path to a <c>VREC2</c> encrypted file.</param>
    /// <param name="decryptedOutputPath">Destination path for the recovered plaintext.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CryptographicException">
    /// Authentication failed for a chunk: the ciphertext was tampered with, chunks were reordered,
    /// or the stream was truncated / extended.
    /// </exception>
    /// <exception cref="InvalidDataException">The file header or frame structure is malformed.</exception>
    public async ValueTask DecryptFileAsync(
        string encryptedInputPath,
        string decryptedOutputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedInputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(decryptedOutputPath);

        EnsureDirectory(decryptedOutputPath);

        await using var input = OpenRead(encryptedInputPath);
        await using var output = OpenWrite(decryptedOutputPath);

        var header = new byte[HeaderSize];
        await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        if (!header.AsSpan(0, MagicSize).SequenceEqual(MagicBytes))
            throw new InvalidDataException("Unrecognized encrypted file (expected VREC2 magic).");

        var noncePrefix = header.AsSpan(MagicSize, NoncePrefixSize).ToArray();
        var chunkSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(MagicSize + NoncePrefixSize));
        if (chunkSize <= 0 || chunkSize > MaxChunkSize)
            throw new InvalidDataException("Encrypted file declares an out-of-range chunk size.");

        var pool = ArrayPool<byte>.Shared;
        var curCipher = pool.Rent(chunkSize);
        var nxtCipher = pool.Rent(chunkSize);
        var plain = pool.Rent(chunkSize);
        var curTag = new byte[TagSize];
        var nxtTag = new byte[TagSize];
        var lengthPrefix = new byte[LengthPrefixSize];

        using var aes = new AesGcm(_key, TagSize);
        try
        {
            var curLen = await ReadFrameAsync(input, curCipher, curTag, lengthPrefix, chunkSize, ct)
                .ConfigureAwait(false);
            if (curLen < 0)
                throw new InvalidDataException("Encrypted file contains no chunks.");

            long index = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var nxtLen = await ReadFrameAsync(input, nxtCipher, nxtTag, lengthPrefix, chunkSize, ct)
                    .ConfigureAwait(false);
                var isFinal = nxtLen < 0;

                DecryptChunk(
                    aes,
                    noncePrefix,
                    (uint)index,
                    index,
                    isFinal,
                    curCipher.AsSpan(0, curLen),
                    curTag,
                    plain.AsSpan(0, curLen));

                await output.WriteAsync(plain.AsMemory(0, curLen), ct).ConfigureAwait(false);

                if (isFinal)
                    break;

                (curCipher, nxtCipher) = (nxtCipher, curCipher);
                (curTag, nxtTag) = (nxtTag, curTag);
                curLen = nxtLen;
                index++;
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain.AsSpan(0, chunkSize));
            pool.Return(curCipher);
            pool.Return(nxtCipher);
            pool.Return(plain);
        }
    }

    private void EncryptChunk(
        AesGcm aes,
        ReadOnlySpan<byte> noncePrefix,
        uint counter,
        long chunkIndex,
        bool isFinal,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(noncePrefix, counter, nonce);

        Span<byte> aad = stackalloc byte[AadSize];
        BuildAad(chunkIndex, isFinal, aad);

        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    private void DecryptChunk(
        AesGcm aes,
        ReadOnlySpan<byte> noncePrefix,
        uint counter,
        long chunkIndex,
        bool isFinal,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(noncePrefix, counter, nonce);

        Span<byte> aad = stackalloc byte[AadSize];
        BuildAad(chunkIndex, isFinal, aad);

        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
    }

    private static void BuildNonce(ReadOnlySpan<byte> noncePrefix, uint counter, Span<byte> nonce)
    {
        noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(NoncePrefixSize), counter);
    }

    private static void BuildAad(long chunkIndex, bool isFinal, Span<byte> aad)
    {
        MagicBytes.AsSpan().CopyTo(aad);
        BinaryPrimitives.WriteUInt64BigEndian(aad.Slice(MagicSize), (ulong)chunkIndex);
        aad[MagicSize + sizeof(ulong)] = isFinal ? (byte)1 : (byte)0;
    }

    private static async ValueTask<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
        => await stream
            .ReadAtLeastAsync(buffer.AsMemory(0, ChunkSize), ChunkSize, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Reads a single frame (length || tag || ciphertext). Returns the ciphertext length, or
    /// <c>-1</c> when the stream ends cleanly on a frame boundary.
    /// </summary>
    private static async ValueTask<int> ReadFrameAsync(
        Stream stream,
        byte[] cipherBuffer,
        byte[] tagBuffer,
        byte[] lengthPrefix,
        int maxChunkSize,
        CancellationToken ct)
    {
        var read = await stream
            .ReadAtLeastAsync(lengthPrefix.AsMemory(0, LengthPrefixSize), LengthPrefixSize, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        if (read == 0)
            return -1;
        if (read < LengthPrefixSize)
            throw new InvalidDataException("Truncated chunk length prefix.");

        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);
        if (length < 0 || length > maxChunkSize)
            throw new InvalidDataException("Chunk length exceeds the declared chunk size.");

        try
        {
            await stream.ReadExactlyAsync(tagBuffer.AsMemory(0, TagSize), ct).ConfigureAwait(false);
            await stream.ReadExactlyAsync(cipherBuffer.AsMemory(0, length), ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Truncated chunk body.", ex);
        }

        return length;
    }

    private static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static FileStream OpenRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenWrite(string path)
        => new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 8192,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
}
