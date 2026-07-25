using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Cryptographically strong random values for RTP session initialisation (SSRC, sequence number, timestamp).
/// RFC 3550 §8.1 / security considerations require these to be drawn from the full range with a CSPRNG so an
/// off-path attacker cannot predict them to inject or spoof packets — a non-crypto PRNG (Random) is unsuitable,
/// and (uint)Random.Next() also never sets the high bit (31-bit range).
/// </summary>
internal static class RtpRandom
{
    /// <summary>A cryptographically strong 32-bit value across the full 0..2^32-1 range.</summary>
    public static uint NextUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>
    /// A cryptographically strong non-zero SSRC (RFC 3550 §8.1), optionally distinct from one already assigned so
    /// two tracks on a shared transport never collide.
    /// </summary>
    public static uint NextSsrc(uint? distinctFrom = null)
    {
        uint ssrc;
        do
        {
            ssrc = NextUInt32();
        }
        while (ssrc == 0 || ssrc == distinctFrom);
        return ssrc;
    }
}
