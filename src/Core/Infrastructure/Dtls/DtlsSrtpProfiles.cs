using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Maps between the DTLS <c>use_srtp</c> protection-profile code points (RFC 5764 §4.1.2)
/// and the SRTP crypto suites implemented by the media layer. Single source of truth for
/// which profiles the SDK offers and accepts during the DTLS-SRTP handshake.
/// </summary>
internal static class DtlsSrtpProfiles
{
    /// <summary>
    /// Profiles offered in the client hello / accepted by the server, in preference order
    /// (RFC 5764 §4.1.2). Both map onto the AES-CM-128 SRTP engine already used for SDES.
    /// </summary>
    public static readonly int[] Supported =
    {
        SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80,
        SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_32,
    };

    /// <summary>
    /// Maps a negotiated <c>use_srtp</c> protection profile to the implemented crypto suite.
    /// </summary>
    /// <exception cref="DtlsSrtpHandshakeException">The profile is not supported.</exception>
    public static SrtpCryptoSuite ToCryptoSuite(int protectionProfile) => protectionProfile switch
    {
        SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80 => SrtpCryptoSuite.AesCm128HmacSha1_80,
        SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_32 => SrtpCryptoSuite.AesCm128HmacSha1_32,
        _ => throw new DtlsSrtpHandshakeException(
            $"Negotiated SRTP protection profile 0x{protectionProfile:X4} is not supported."),
    };

    /// <summary>
    /// Picks the first locally supported profile from the peer's offered list, preserving
    /// the local preference order in <see cref="Supported"/>. Returns <see langword="null"/>
    /// when there is no overlap.
    /// </summary>
    public static int? SelectFromOffered(int[] offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        foreach (var candidate in Supported)
        {
            if (Array.IndexOf(offered, candidate) >= 0)
                return candidate;
        }

        return null;
    }
}
