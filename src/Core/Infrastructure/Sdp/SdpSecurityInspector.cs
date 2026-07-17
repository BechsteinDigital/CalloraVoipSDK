using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp;

/// <summary>
/// Inspects SDP audio sections for SRTP-relevant profile and key-management attributes.
/// This class performs pure SDP interpretation and intentionally contains no policy rules.
/// </summary>
internal static class SdpSecurityInspector
{
    private static readonly ISdpSessionParser Parser = new SdpSessionParser();

    /// <summary>
    /// Parses SDP and returns SRTP signal state for the first active audio m-line.
    /// </summary>
    public static bool TryInspectAudioSecurity(
        string? sdp,
        out bool isSrtpSignaled,
        out string mediaProfile,
        ILogger? logger = null)
    {
        isSrtpSignaled = false;
        mediaProfile = string.Empty;

        if (string.IsNullOrWhiteSpace(sdp))
            return false;

        try
        {
            var parsed = Parser.Parse(sdp);
            var audio = parsed.Media.FirstOrDefault(
                m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase)
                     && !m.Disabled
                     && m.Port > 0);
            if (audio is null)
                return false;

            mediaProfile = audio.Profile;
            isSrtpSignaled = IsSrtpSignaled(parsed, audio);
            return true;
        }
        catch (Exception ex)
        {
            // Untrusted remote SDP: an unparseable body yields "no SRTP signal determinable".
            // Broad by design (must not crash the SRTP policy guard) but logged (HARD-G3).
            logger?.LogDebug(ex, "Discarding unparseable remote SDP during SRTP security inspection.");
            return false;
        }
    }

    /// <summary>
    /// Returns true when one audio media section signals secure RTP profile/attributes.
    /// </summary>
    public static bool IsSrtpSignaled(SdpSessionDescription session, SdpMediaDescription audio)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(audio);

        if (!IsSecureProfile(audio.Profile))
            return false;

        if (audio.Crypto.Count > 0)
            return true;

        if (audio.Fingerprint is not null || session.Fingerprint is not null)
            return true;

        if (!string.IsNullOrWhiteSpace(audio.DtlsSetup) || !string.IsNullOrWhiteSpace(session.DtlsSetup))
            return true;

        // Keep SAVP/SAVPF-only profiles as secure signals, even if keying attributes
        // are absent in malformed SDP.
        return true;
    }

    /// <summary>
    /// Returns true when profile token indicates secure RTP transport.
    /// </summary>
    public static bool IsSecureProfile(string? profile) =>
        !string.IsNullOrWhiteSpace(profile)
        && profile.Contains("SAVP", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the profile token indicates a DTLS transport (<c>UDP/TLS/…</c>,
    /// RFC 5764). Such a profile is fingerprint-keyed; any <c>a=crypto</c> on it is ignored.
    /// </summary>
    public static bool IsDtlsProfile(string? profile) =>
        !string.IsNullOrWhiteSpace(profile)
        && profile.StartsWith("UDP/TLS/", StringComparison.OrdinalIgnoreCase);
}
