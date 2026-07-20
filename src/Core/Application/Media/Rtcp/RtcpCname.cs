using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Application.Media.Rtcp;

/// <summary>
/// Generates the RTCP SDES CNAME (RFC 3550 §6.5.1) as a per-session, random, opaque identifier per RFC 7022.
/// <para>
/// The CNAME must be stable for the lifetime of one session and unique enough to bind an SSRC to its source,
/// but it must not carry personally identifying information and must not let a passive observer correlate
/// separate sessions from the same installation. A machine name (the historical <c>voipsdk-{MachineName}</c>)
/// fails both: it can leak the host identity and is identical across every session on that host. RFC 7022
/// therefore recommends a cryptographically random per-session value; 96 bits of randomness is well above its
/// uniqueness guidance while staying short on the wire.
/// </para>
/// <para>
/// Placed in the application layer so both the SIP call RTCP monitor and the WebRTC BUNDLE reporter share one
/// generator without an infrastructure-layer dependency.
/// </para>
/// </summary>
internal static class RtcpCname
{
    private const int RandomBytes = 12; // 96 bits — RFC 7022 §4.3 short-term persistent, comfortably unique.

    /// <summary>
    /// Creates a fresh opaque CNAME: 96 random bits, base64url without padding (SDES-safe ASCII). Never derived
    /// from the machine name, user, or any host attribute, and distinct on every call.
    /// </summary>
    public static string NewOpaque()
    {
        Span<byte> bytes = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
