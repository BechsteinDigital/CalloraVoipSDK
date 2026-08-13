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
    /// Parses SDP and returns the SRTP signal state across <em>all</em> active audio m-lines.
    /// </summary>
    /// <remarks>
    /// #160 P2-17: this used to inspect only the first active audio section, which made the answer a
    /// property of m-line order rather than of the offer. Under BUNDLE or any multi-track offer a
    /// second audio m-line on a plain <c>RTP/AVP</c> profile went unseen, so an offer whose first
    /// section was <c>RTP/SAVP</c> reported "SRTP signalled" while carrying an unencrypted audio
    /// stream beside it — enough to walk past an <c>SrtpPolicy.Required</c> guard.
    /// The signal is now conjunctive: every active audio section must signal SRTP.
    /// </remarks>
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
            var audioSections = parsed.Media
                .Where(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase)
                            && !m.Disabled
                            && m.Port > 0)
                .ToArray();
            if (audioSections.Length == 0)
                return false;

            // The profile reported back is the one that carries the decision: the first section that
            // does NOT signal SRTP, so telemetry and the 488 reason name the weak leg rather than a
            // secure sibling that happened to come first.
            var firstInsecure = audioSections.FirstOrDefault(m => !IsSrtpSignaled(parsed, m));
            isSrtpSignaled = firstInsecure is null;
            mediaProfile = (firstInsecure ?? audioSections[0]).Profile;
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
