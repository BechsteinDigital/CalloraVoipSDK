using System.Net;
using System.Net.Sockets;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

/// <summary>
/// Serializes SDP session models to wire text (RFC 4566, updated by RFC 8866).
/// Emits ICE attributes (RFC 8839), SDES crypto (RFC 4568),
/// rtcp-mux (RFC 5761), MID/BUNDLE (RFC 5888), and fmtp (RFC 4566 §6.6).
/// </summary>
internal sealed class SdpSessionSerializer : ISdpSessionSerializer
{
    /// <inheritdoc />
    public string Serialize(SdpSessionDescription session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sb = new StringBuilder();

        // --- Session header ---
        sb.AppendLine("v=0");
        sb.AppendLine($"o=- {session.SessionId} {session.SessionVersion} IN {GetNetType(session.OriginAddress)} {session.OriginAddress}");
        sb.AppendLine("s=CalloraVoipSdk");
        sb.AppendLine($"c=IN {GetNetType(session.ConnectionAddress)} {session.ConnectionAddress}");
        sb.AppendLine("t=0 0");

        // Session-level ICE credentials (RFC 8839 §5.4)
        if (session.IceUfrag is not null)
            sb.AppendLine($"a=ice-ufrag:{session.IceUfrag}");
        if (session.IcePwd is not null)
            sb.AppendLine($"a=ice-pwd:{session.IcePwd}");
        if (session.IceOptions is not null)
            sb.AppendLine($"a=ice-options:{session.IceOptions}");

        // Session-level DTLS fingerprint / setup (RFC 8122 / RFC 4145)
        if (session.Fingerprint is not null)
            sb.AppendLine($"a=fingerprint:{session.Fingerprint.Serialize()}");
        if (session.DtlsSetup is not null)
            sb.AppendLine($"a=setup:{session.DtlsSetup}");

        // BUNDLE group (RFC 5888)
        if (session.Group is not null)
            sb.AppendLine($"a=group:{session.Group}");

        // Session-level direction
        AppendDirection(sb, session.SessionDirection);

        // --- Media sections ---
        foreach (var media in session.Media)
        {
            AppendMediaSection(sb, media, session.ConnectionAddress);
        }

        // SDP wire format requires CRLF line endings (RFC 4566 §5)
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Media section
    // -------------------------------------------------------------------------

    private static void AppendMediaSection(StringBuilder sb, SdpMediaDescription media, string sessionConnectionAddress)
    {
        // m= line
        var payloads = string.Join(" ", media.Codecs.Select(c => c.PayloadType.ToString()));
        sb.AppendLine($"m={media.MediaType} {media.Port} {media.Profile} {payloads}");

        // Per-media c= override (RFC 4566 §5.7) — only emit when different from session level
        if (media.ConnectionAddress is not null &&
            !string.Equals(media.ConnectionAddress, sessionConnectionAddress, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"c=IN {GetNetType(media.ConnectionAddress)} {media.ConnectionAddress}");

        // Bandwidth (RFC 4566 §5.8)
        if (media.Bandwidth.HasValue)
            sb.AppendLine($"b=AS:{media.Bandwidth.Value}");

        // MID (RFC 5888)
        if (media.Mid is not null)
            sb.AppendLine($"a=mid:{media.Mid}");

        // DTLS fingerprint / setup (RFC 8122 / RFC 4145) — media-level overrides session
        if (media.Fingerprint is not null)
            sb.AppendLine($"a=fingerprint:{media.Fingerprint.Serialize()}");
        if (media.DtlsSetup is not null)
            sb.AppendLine($"a=setup:{media.DtlsSetup}");

        // SDES crypto (RFC 4568) — before direction
        foreach (var crypto in media.Crypto)
            sb.AppendLine($"a=crypto:{crypto.Serialize()}");

        // rtpmap lines
        foreach (var codec in media.Codecs)
        {
            var channels = codec.Channels > 1 ? $"/{codec.Channels}" : string.Empty;
            sb.AppendLine($"a=rtpmap:{codec.PayloadType} {codec.Name}/{codec.ClockRate}{channels}");
        }

        // fmtp lines (RFC 4566 §6.6)
        foreach (var fmtp in media.Fmtp)
            sb.AppendLine($"a=fmtp:{fmtp.PayloadType} {fmtp.Parameters}");

        // RTCP feedback (RFC 4585 §4.2)
        foreach (var feedback in media.RtcpFeedback)
            sb.AppendLine($"a=rtcp-fb:{feedback.Serialize()}");

        // Direction
        AppendDirection(sb, media.Direction);

        // ptime / maxptime
        if (media.Ptime.HasValue)
            sb.AppendLine($"a=ptime:{media.Ptime.Value}");
        if (media.MaxPtime.HasValue)
            sb.AppendLine($"a=maxptime:{media.MaxPtime.Value}");

        // RTCP (RFC 5761 / RFC 3605)
        if (media.RtcpMux)
            sb.AppendLine("a=rtcp-mux");
        if (media.RtcpPort.HasValue)
            sb.AppendLine($"a=rtcp:{media.RtcpPort.Value}");

        // ICE credentials (RFC 8839)
        if (media.IceUfrag is not null)
            sb.AppendLine($"a=ice-ufrag:{media.IceUfrag}");
        if (media.IcePwd is not null)
            sb.AppendLine($"a=ice-pwd:{media.IcePwd}");
        if (media.IceOptions is not null)
            sb.AppendLine($"a=ice-options:{media.IceOptions}");

        // ICE candidates (RFC 8839)
        foreach (var candidate in media.Candidates)
            sb.AppendLine($"a=candidate:{candidate.Serialize()}");

        // end-of-candidates (RFC 8840)
        if (media.EndOfCandidates)
            sb.AppendLine("a=end-of-candidates");
    }

    // -------------------------------------------------------------------------
    // Direction
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Address family
    // -------------------------------------------------------------------------

    private static string GetNetType(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "IP6"
            : "IP4";

    private static void AppendDirection(StringBuilder sb, SdpMediaDirection direction)
    {
        var token = direction switch
        {
            SdpMediaDirection.SendOnly => "sendonly",
            SdpMediaDirection.RecvOnly => "recvonly",
            SdpMediaDirection.Inactive => "inactive",
            _ => "sendrecv"
        };
        sb.AppendLine($"a={token}");
    }
}
