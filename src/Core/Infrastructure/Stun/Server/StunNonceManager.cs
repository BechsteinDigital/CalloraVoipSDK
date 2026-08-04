using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Server;

/// <summary>
/// Thread-safe, <b>stateless</b> implementation of <see cref="IStunNonceManager"/> for the
/// long-term credential mechanism (RFC 5389 §10.2.2).
/// <para>
/// A nonce is self-describing — <c>salt || issued-timestamp || HMAC-SHA256(secret, salt||timestamp)</c>,
/// Base64-encoded — so validity is decided purely by recomputing the MAC and checking the embedded
/// timestamp against the TTL. Nothing is stored per issued nonce (RFC 7616 §5.4 style), which removes
/// the amplification vector where an unauthenticated peer floods the server with requests that each
/// mint and retain a nonce: challenge issuance no longer grows server memory (K4 Wire-DoS-Cap).
/// </para>
/// <para>
/// The signing secret is random per instance, so nonces do not survive a restart (a client simply
/// receives 438 Stale Nonce and refreshes) and a nonce minted by one manager is never valid on
/// another.
/// </para>
/// </summary>
internal sealed class StunNonceManager : IStunNonceManager
{
    private const int SaltLength = 8;
    private const int TimestampLength = 8;
    private const int MacLength = 16;                 // 128-bit truncated HMAC — ample against forgery.
    private const int NonceByteLength = SaltLength + TimestampLength + MacLength;

    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly TimeSpan _nonceTtl;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Default nonce lifetime recommended by RFC 5389 §10.2.2.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>Initialises with the default 5-minute nonce lifetime.</summary>
    public StunNonceManager() : this(DefaultTtl) { }

    /// <summary>Initialises with a custom nonce lifetime.</summary>
    /// <param name="nonceTtl">
    /// How long a nonce remains valid after issuance.
    /// Shorter values improve security; longer values reduce retransmit cost.
    /// </param>
    public StunNonceManager(TimeSpan nonceTtl) : this(nonceTtl, static () => DateTimeOffset.UtcNow) { }

    /// <summary>Initialises with a custom lifetime and an injectable clock (for deterministic tests).</summary>
    internal StunNonceManager(TimeSpan nonceTtl, Func<DateTimeOffset> clock)
    {
        if (nonceTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(nonceTtl), "Nonce TTL must be positive.");

        ArgumentNullException.ThrowIfNull(clock);
        _nonceTtl = nonceTtl;
        _clock = clock;
    }

    /// <inheritdoc />
    public string GenerateNonce()
    {
        Span<byte> buffer = stackalloc byte[NonceByteLength];
        RandomNumberGenerator.Fill(buffer[..SaltLength]);
        BinaryPrimitives.WriteInt64BigEndian(buffer.Slice(SaltLength, TimestampLength), _clock().UtcTicks);
        ComputeMac(buffer[..(SaltLength + TimestampLength)], buffer[(SaltLength + TimestampLength)..]);
        return Convert.ToBase64String(buffer);
    }

    /// <inheritdoc />
    public bool IsNonceValid(string nonce)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;

        Span<byte> buffer = stackalloc byte[NonceByteLength];
        if (!Convert.TryFromBase64String(nonce, buffer, out var written) || written != NonceByteLength)
            return false;

        // Recompute the MAC over salt||timestamp and compare in constant time (K5): a mismatch means
        // the nonce was not minted by this manager (forged, tampered, or from another instance).
        Span<byte> expectedMac = stackalloc byte[MacLength];
        ComputeMac(buffer[..(SaltLength + TimestampLength)], expectedMac);
        if (!CryptographicOperations.FixedTimeEquals(buffer[(SaltLength + TimestampLength)..], expectedMac))
            return false;

        // The MAC gate above guarantees this timestamp was written by this manager, so issuedTicks is a
        // real UtcTicks value — an attacker cannot craft an extreme value to overflow the subtraction.
        var issuedTicks = BinaryPrimitives.ReadInt64BigEndian(buffer.Slice(SaltLength, TimestampLength));
        var ageTicks = _clock().UtcTicks - issuedTicks;
        // Reject future-dated nonces (clock rewind) and anything past the TTL.
        return ageTicks >= 0 && ageTicks <= _nonceTtl.Ticks;
    }

    private void ComputeMac(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        Span<byte> full = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_secret, data, full);
        full[..MacLength].CopyTo(destination);
    }
}
