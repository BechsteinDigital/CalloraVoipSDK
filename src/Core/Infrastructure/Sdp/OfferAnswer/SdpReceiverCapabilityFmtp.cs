using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Removes receiver-capability <c>fmtp</c> parameters from a <c>sendonly</c> answer (RFC 6184 §8.2.2).
/// </summary>
/// <remarks>
/// The answer carries the offer's fmtp forward, which for <c>profile-level-id</c> and
/// <c>packetization-mode</c> is not merely allowed but required — §8.2.2 makes those symmetric, so an
/// answerer either keeps them or drops the payload type entirely. The capability parameters are the
/// opposite case: they declare what the sender of that SDP is willing to <em>receive</em>, and the RFC is
/// explicit that they
/// <blockquote>MUST NOT be present when the direction attribute is "sendonly"</blockquote>
/// Carrying them into a sendonly answer therefore states a receiving limit on a line that receives
/// nothing — and states one copied from the peer at that.
/// <para>
/// Deliberately not filtered: <c>sprop-*</c> describe the stream the sender <em>emits</em> (§8.2.2 calls
/// this out as differing from the usual receiver-oriented reading), and <c>profile-level-id</c> /
/// <c>packetization-mode</c> must survive untouched. The list below is the one §8.2.2 enumerates,
/// nothing added by inference.
/// </para>
/// </remarks>
internal static class SdpReceiverCapabilityFmtp
{
    private static readonly string[] CapabilityParameters =
    [
        "max-mbps", "max-smbps", "max-fs", "max-cpb", "max-dpb", "max-br",
        "redundant-pic-cap", "max-rcmd-nalu-size", "sar-understood", "sar-supported",
    ];

    /// <summary>
    /// Strips the receiver-capability parameters when <paramref name="answerDirection"/> is
    /// <see cref="SdpMediaDirection.SendOnly"/>; returns <paramref name="fmtp"/> unchanged otherwise.
    /// An entry left with no parameters is dropped rather than emitted empty.
    /// </summary>
    public static IReadOnlyList<SdpFmtpAttribute> StripForSendOnly(
        IEnumerable<SdpFmtpAttribute> fmtp,
        SdpMediaDirection answerDirection)
    {
        ArgumentNullException.ThrowIfNull(fmtp);

        if (answerDirection != SdpMediaDirection.SendOnly)
            return fmtp as IReadOnlyList<SdpFmtpAttribute> ?? [.. fmtp];

        var result = new List<SdpFmtpAttribute>();
        foreach (var entry in fmtp)
        {
            var kept = Strip(entry.Parameters);
            if (kept.Length == 0)
                continue;

            result.Add(entry.Parameters.Equals(kept, StringComparison.Ordinal)
                ? entry
                : new SdpFmtpAttribute { PayloadType = entry.PayloadType, Parameters = kept });
        }

        return result;
    }

    /// <summary>
    /// Drops capability parameters from one semicolon-separated fmtp value, preserving the order and
    /// spelling of everything else.
    /// </summary>
    private static string Strip(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return string.Empty;

        var kept = new List<string>();
        foreach (var part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            // A parameter without '=' is not one of the capability parameters (all of them carry a
            // value), so it stays — the filter must not eat tokens it does not recognise.
            var eq = trimmed.IndexOf('=');
            var name = eq < 0 ? trimmed : trimmed[..eq].Trim();

            if (!CapabilityParameters.Contains(name, StringComparer.OrdinalIgnoreCase))
                kept.Add(trimmed);
        }

        return string.Join(";", kept);
    }
}
