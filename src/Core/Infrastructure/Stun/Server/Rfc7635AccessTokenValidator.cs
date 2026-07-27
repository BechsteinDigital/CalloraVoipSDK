using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Server;

/// <summary>
/// RFC 7635 validator for STUN ACCESS-TOKEN attributes.
/// <para>
/// Supported token format:
/// <code>
/// struct {
///   uint16 nonce_length;
///   opaque nonce[nonce_length];
///   opaque encrypted_block;
/// } token;
/// </code>
/// where <c>encrypted_block</c> is AEAD ciphertext of:
/// <code>
/// struct {
///   uint16 key_length;
///   opaque mac_key[key_length];
///   uint64 timestamp;
///   uint32 lifetime;
/// } plaintext;
/// </code>
/// using associated data A = STUN server name (RFC 7635 §6.2, §7).
/// </para>
/// </summary>
internal sealed class Rfc7635AccessTokenValidator : IStunAccessTokenValidator
{
    private readonly IStunThirdPartyKeyProvider _keyProvider;
    private readonly string _serverName;
    private readonly byte[] _serverNameBytes;
    private readonly TimeSpan _allowedClockSkew;
    private readonly int _maxTokenSizeBytes;
    private readonly bool _acceptBase64EncodedTokens;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger<Rfc7635AccessTokenValidator> _logger;

    /// <summary>
    /// Creates a validator with explicit options.
    /// </summary>
    public Rfc7635AccessTokenValidator(
        Rfc7635AccessTokenValidatorOptions options,
        ILogger<Rfc7635AccessTokenValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServerName);
        ArgumentNullException.ThrowIfNull(options.KeyProvider);

        if (options.AllowedClockSkew < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "AllowedClockSkew must be >= 0.");
        if (options.MaxTokenSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTokenSizeBytes must be positive.");

        _keyProvider = options.KeyProvider;
        _serverName = options.ServerName;
        _serverNameBytes = Encoding.UTF8.GetBytes(_serverName);
        _allowedClockSkew = options.AllowedClockSkew;
        _maxTokenSizeBytes = options.MaxTokenSizeBytes;
        _acceptBase64EncodedTokens = options.AcceptBase64EncodedTokens;
        _utcNow = options.UtcNow ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
    }

    /// <inheritdoc />
    public bool TryResolveHmacKey(ReadOnlyMemory<byte> accessToken, string username, out byte[] hmacKey)
    {
        hmacKey = [];

        if (string.IsNullOrWhiteSpace(username))
        {
            _logger.LogWarning("RFC7635 token validation failed: USERNAME/kid missing");
            return false;
        }

        if (accessToken.IsEmpty)
        {
            _logger.LogWarning("RFC7635 token validation failed for kid {Kid}: ACCESS-TOKEN missing", username);
            return false;
        }

        if (!_keyProvider.TryGetKeyMaterial(username, out var keyMaterial))
        {
            _logger.LogWarning("RFC7635 token validation failed: unknown kid {Kid}", username);
            return false;
        }

        try
        {
            if (!TryNormalizeToken(accessToken.Span, out var normalizedToken))
                return false;

            if (!TryParseOuterToken(normalizedToken, out var nonce, out var encryptedBlock))
                return false;

            if (!TryDecryptEncryptedBlock(keyMaterial, nonce, encryptedBlock, out var plaintext))
                return false;

            if (!TryParsePlaintext(plaintext, out var parsedMacKey, out var issuedAt, out var lifetimeSeconds))
                return false;

            if (!IsWithinLifetime(issuedAt, lifetimeSeconds, _utcNow(), _allowedClockSkew))
            {
                _logger.LogWarning(
                    "RFC7635 token validation failed for kid {Kid}: token outside lifetime window",
                    username);
                return false;
            }

            hmacKey = parsedMacKey;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RFC7635 token validation failed unexpectedly for kid {Kid}", username);
            hmacKey = [];
            return false;
        }
    }

    private bool TryNormalizeToken(ReadOnlySpan<byte> accessToken, out byte[] normalizedToken)
    {
        normalizedToken = accessToken.ToArray();

        if (_acceptBase64EncodedTokens
            && LooksLikeAscii(accessToken)
            && TryBase64Decode(accessToken, out var decoded))
        {
            normalizedToken = decoded;
        }

        if (normalizedToken.Length > _maxTokenSizeBytes)
        {
            _logger.LogWarning(
                "RFC7635 token validation failed: token length {Length} exceeds limit {Limit}",
                normalizedToken.Length,
                _maxTokenSizeBytes);
            return false;
        }

        if (normalizedToken.Length < 3) // nonce-length(2) + at least 1 byte encrypted block
        {
            _logger.LogWarning("RFC7635 token validation failed: token is too short");
            return false;
        }

        return true;
    }

    private static bool LooksLikeAscii(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            if (b is < 0x20 or > 0x7E)
                return false;
        }

        return true;
    }

    private static bool TryBase64Decode(ReadOnlySpan<byte> encoded, out byte[] decoded)
    {
        decoded = [];
        var text = Encoding.ASCII.GetString(encoded).Trim();
        if (text.Length == 0)
            return false;

        var max = (text.Length / 4 + 1) * 3;
        var buffer = new byte[max];
        if (!Convert.TryFromBase64String(text, buffer, out int bytesWritten))
            return false;

        decoded = buffer[..bytesWritten];
        return bytesWritten > 0;
    }

    private bool TryParseOuterToken(
        byte[] token,
        out byte[] nonce,
        out byte[] encryptedBlock)
    {
        nonce = [];
        encryptedBlock = [];

        if (token.Length < 3)
            return false;

        ushort nonceLength = BinaryPrimitives.ReadUInt16BigEndian(token);
        int offset = 2;

        if (nonceLength == 0 || token.Length < offset + nonceLength + 1)
        {
            _logger.LogWarning("RFC7635 token validation failed: invalid nonce length");
            return false;
        }

        nonce = token[offset..(offset + nonceLength)].ToArray();
        encryptedBlock = token[(offset + nonceLength)..].ToArray();

        if (encryptedBlock.Length == 0)
        {
            _logger.LogWarning("RFC7635 token validation failed: encrypted block missing");
            return false;
        }

        return true;
    }

    private bool TryDecryptEncryptedBlock(
        StunThirdPartyKeyMaterial keyMaterial,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> encryptedBlock,
        out byte[] plaintext)
    {
        plaintext = [];

        if (keyMaterial.EncryptionAlgorithm != StunThirdPartyTokenEncryptionAlgorithm.Aes256Gcm)
        {
            _logger.LogWarning(
                "RFC7635 token validation failed: unsupported encryption algorithm {Algorithm}",
                keyMaterial.EncryptionAlgorithm);
            return false;
        }

        if (keyMaterial.SymmetricKey is null || keyMaterial.SymmetricKey.Length != 32)
        {
            _logger.LogWarning("RFC7635 token validation failed: A256GCM requires 32-byte key");
            return false;
        }

        if (!IsValidSize(nonce.Length, AesGcm.NonceByteSizes))
        {
            _logger.LogWarning(
                "RFC7635 token validation failed: invalid nonce size {Size} for A256GCM",
                nonce.Length);
            return false;
        }

        int tagSize = keyMaterial.GcmTagSizeBytes;
        if (!IsValidSize(tagSize, AesGcm.TagByteSizes))
        {
            _logger.LogWarning("RFC7635 token validation failed: unsupported GCM tag size {TagSize}", tagSize);
            return false;
        }

        if (encryptedBlock.Length <= tagSize)
        {
            _logger.LogWarning("RFC7635 token validation failed: encrypted block shorter than tag");
            return false;
        }

        var ciphertext = encryptedBlock[..^tagSize];
        var tag = encryptedBlock[^tagSize..];
        plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(keyMaterial.SymmetricKey, tagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, _serverNameBytes);
            return true;
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(
                ex,
                "RFC7635 token validation failed: decryption/authentication failed for server {Server}",
                _serverName);
            plaintext = [];
            return false;
        }
    }

    private bool TryParsePlaintext(
        ReadOnlySpan<byte> plaintext,
        out byte[] macKey,
        out DateTimeOffset issuedAt,
        out uint lifetimeSeconds)
    {
        macKey = [];
        issuedAt = DateTimeOffset.UnixEpoch;
        lifetimeSeconds = 0;

        // key_length(2) + timestamp(8) + lifetime(4)
        if (plaintext.Length < 14)
        {
            _logger.LogWarning("RFC7635 token validation failed: plaintext too short");
            return false;
        }

        ushort keyLength = BinaryPrimitives.ReadUInt16BigEndian(plaintext);
        int expectedLength = 2 + keyLength + 8 + 4;

        if (keyLength == 0 || plaintext.Length != expectedLength)
        {
            _logger.LogWarning(
                "RFC7635 token validation failed: invalid key length ({KeyLength}) for plaintext length {Length}",
                keyLength,
                plaintext.Length);
            return false;
        }

        macKey = plaintext[2..(2 + keyLength)].ToArray();

        ulong timestampRaw = BinaryPrimitives.ReadUInt64BigEndian(plaintext[(2 + keyLength)..]);
        lifetimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(plaintext[(2 + keyLength + 8)..]);

        if (lifetimeSeconds == 0)
        {
            _logger.LogWarning("RFC7635 token validation failed: lifetime must be > 0");
            return false;
        }

        issuedAt = DecodeRfc7635Timestamp(timestampRaw);
        return true;
    }

    // internal for direct wire-decode unit coverage (InternalsVisibleTo); the production caller is
    // TryParsePlaintext above.
    internal static DateTimeOffset DecodeRfc7635Timestamp(ulong raw)
    {
        // RFC 7635 §6.2: the 64-bit timestamp is seconds-since-epoch in the high 48 bits and a
        // 16-bit fraction of a second in the low 16 bits, i.e. the fraction is scaled by 2^16.
        // The divisor is therefore 65536 (1 << 16), not 64000.
        ulong seconds = raw >> 16;
        ulong fractions = raw & 0xFFFF;
        long ticks = (long)((fractions * TimeSpan.TicksPerSecond) / (1UL << 16));
        return DateTimeOffset.UnixEpoch.AddSeconds((long)seconds).AddTicks(ticks);
    }

    private static bool IsWithinLifetime(
        DateTimeOffset issuedAt,
        uint lifetimeSeconds,
        DateTimeOffset now,
        TimeSpan delta)
    {
        // RFC 7635 §7:
        // lifetime + Delta > abs(RDnew - TS)
        var diffSeconds = Math.Abs((now - issuedAt).TotalSeconds);
        return lifetimeSeconds + delta.TotalSeconds > diffSeconds;
    }

    private static bool IsValidSize(int value, KeySizes sizes)
    {
        if (value < sizes.MinSize || value > sizes.MaxSize)
            return false;

        if (sizes.SkipSize == 0)
            return value == sizes.MinSize;

        return (value - sizes.MinSize) % sizes.SkipSize == 0;
    }
}
