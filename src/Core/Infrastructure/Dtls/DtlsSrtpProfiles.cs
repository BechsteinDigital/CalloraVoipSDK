using System.Linq;
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

    // The AEAD-GCM protection profiles (RFC 7714) — recognised for diagnostics only; not implemented.
    private const int SrtpAeadAes128Gcm = 0x0007;
    private const int SrtpAeadAes256Gcm = 0x0008;

    /// <summary>
    /// Builds a human-readable error for a DTLS-SRTP handshake that found no common protection profile, so the
    /// failure diagnoses the config trap instead of surfacing an anonymous <c>insufficient_security</c> alert. If
    /// the peer offered only AEAD-GCM profiles (RFC 7714) — which this SDK does not implement (AES-CM-128 only) —
    /// the message says so explicitly, since that is the common Firefox-only-GCM interop failure.
    /// </summary>
    public static string FormatNoCommonProfileError(int[] offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var offeredHex = offered.Length == 0 ? "(none)" : string.Join(", ", offered.Select(p => $"0x{p:X4}"));
        var onlyGcm = offered.Length > 0
            && offered.All(p => p is SrtpAeadAes128Gcm or SrtpAeadAes256Gcm);
        var hint = onlyGcm
            ? " The peer offered only AEAD-GCM profiles (RFC 7714), which this SDK does not implement — it supports " +
              "AES-CM-128 (0x0001/0x0002) only. Configure the peer to offer AES-CM (e.g. Firefox's default profile set)."
            : string.Empty;
        return $"No common DTLS-SRTP protection profile: peer offered [{offeredHex}], this SDK supports " +
               $"AES-CM-128 (0x0001/0x0002) only.{hint}";
    }
}
