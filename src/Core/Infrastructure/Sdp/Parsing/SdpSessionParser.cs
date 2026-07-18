using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

/// <summary>
/// SDP parser covering RFC 4566 (updated by RFC 8866) with extensions for
/// RFC 3264 offer/answer, RFC 4568 (SDES), RFC 5761 (rtcp-mux),
/// RFC 5888 (BUNDLE / MID), and RFC 8839 (ICE).
/// </summary>
internal sealed class SdpSessionParser : ISdpSessionParser
{
    /// <inheritdoc />
    public SdpSessionDescription Parse(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
            throw new ArgumentException("SDP cannot be empty.", nameof(sdp));

        var lines = sdp.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string originAddress = "127.0.0.1";
        string connectionAddress = "127.0.0.1";
        var sessionDirection = SdpMediaDirection.SendRecv;
        string? sessionGroup = null;
        string? sessionIceUfrag = null;
        string? sessionIcePwd = null;
        string? sessionIceOptions = null;
        SdpFingerprint? sessionFingerprint = null;
        string? sessionDtlsSetup = null;

        var media = new List<SdpMediaDescription>();
        MediaBuilder? current = null;

        foreach (var line in lines)
        {
            if (line.Length < 2 || line[1] != '=')
                continue;

            var type = line[0];
            var value = line[2..];

            switch (type)
            {
                case 'o':
                    originAddress = ParseAddressTail(value) ?? originAddress;
                    break;

                case 'c':
                    var addr = ParseConnectionAddress(value);
                    if (!string.IsNullOrWhiteSpace(addr))
                    {
                        if (current is null)
                            connectionAddress = addr;
                        else
                            current.ConnectionAddress = addr;
                    }
                    break;

                case 'b':
                    if (current is not null)
                        current.Bandwidth = ParseBandwidth(value);
                    break;

                case 'm':
                    if (current is not null)
                        media.Add(current.Build(sessionDirection, connectionAddress));
                    current = ParseMediaLine(value);
                    break;

                case 'a':
                    ParseAttribute(
                        value,
                        current,
                        ref sessionDirection,
                        ref sessionGroup,
                        ref sessionIceUfrag,
                        ref sessionIcePwd,
                        ref sessionIceOptions,
                        ref sessionFingerprint,
                        ref sessionDtlsSetup);
                    break;
            }
        }

        if (current is not null)
            media.Add(current.Build(sessionDirection, connectionAddress));

        return new SdpSessionDescription
        {
            OriginAddress = originAddress,
            ConnectionAddress = connectionAddress,
            SessionDirection = sessionDirection,
            Media = media,
            Group = sessionGroup,
            IceUfrag = sessionIceUfrag,
            IcePwd = sessionIcePwd,
            IceOptions = sessionIceOptions,
            Fingerprint = sessionFingerprint,
            DtlsSetup = sessionDtlsSetup
        };
    }

    // -------------------------------------------------------------------------
    // Attribute dispatcher
    // -------------------------------------------------------------------------

    private static void ParseAttribute(
        string value,
        MediaBuilder? current,
        ref SdpMediaDirection sessionDirection,
        ref string? sessionGroup,
        ref string? sessionIceUfrag,
        ref string? sessionIcePwd,
        ref string? sessionIceOptions,
        ref SdpFingerprint? sessionFingerprint,
        ref string? sessionDtlsSetup)
    {
        // Colon-separated attributes: a=name:val
        var colonIndex = value.IndexOf(':');
        var name = colonIndex > 0 ? value[..colonIndex] : value;
        var attrValue = colonIndex > 0 ? value[(colonIndex + 1)..] : string.Empty;

        switch (name.ToLowerInvariant())
        {
            // --- direction ---
            case "sendrecv":
            case "sendonly":
            case "recvonly":
            case "inactive":
            {
                var dir = ParseDirectionToken(name);
                if (dir.HasValue)
                {
                    if (current is null)
                        sessionDirection = dir.Value;
                    else
                        current.Direction = dir.Value;
                }
                break;
            }

            // --- rtpmap ---
            case "rtpmap" when current is not null:
            {
                var codec = ParseRtpMap(attrValue);
                if (codec is not null)
                    current.Codecs[codec.PayloadType] = codec;
                break;
            }

            // --- fmtp ---
            case "fmtp" when current is not null:
            {
                var fmtp = SdpFmtpAttribute.TryParse(attrValue);
                if (fmtp is not null)
                    current.Fmtp.Add(fmtp);
                break;
            }

            // --- RTCP feedback (RFC 4585 §4.2) ---
            case "rtcp-fb" when current is not null:
            {
                var feedback = SdpRtcpFeedback.TryParse(attrValue);
                if (feedback is not null)
                    current.RtcpFeedback.Add(feedback);
                break;
            }

            // --- RTP header extension mapping (RFC 8285 §5) ---
            case "extmap" when current is not null:
            {
                var extmap = SdpExtmap.TryParse(attrValue);
                if (extmap is not null)
                    current.Extensions.Add(extmap);
                break;
            }

            // --- ptime / maxptime ---
            case "ptime" when current is not null && int.TryParse(attrValue, out var ptime):
                current.Ptime = ptime;
                break;

            case "maxptime" when current is not null && int.TryParse(attrValue, out var maxPtime):
                current.MaxPtime = maxPtime;
                break;

            // --- RTCP (RFC 5761 / RFC 3605) ---
            case "rtcp-mux" when current is not null:
                current.RtcpMux = true;
                break;

            case "rtcp" when current is not null && int.TryParse(attrValue.Split(' ')[0], out var rtcpPort):
                current.RtcpPort = rtcpPort;
                break;

            // --- MID (RFC 5888) ---
            case "mid" when current is not null && !string.IsNullOrWhiteSpace(attrValue):
                current.Mid = attrValue.Trim();
                break;

            // --- MSID (RFC 8830): MediaStream / track identity ---
            case "msid" when current is not null:
                current.Msid = SdpMsid.TryParse(attrValue);
                break;

            // --- Simulcast (RFC 8851 rid / RFC 8853 simulcast) ---
            case "rid" when current is not null:
            {
                var rid = SdpRid.TryParse(attrValue);
                if (rid is not null)
                    current.Rids.Add(rid);
                break;
            }

            case "simulcast" when current is not null:
                current.Simulcast = SdpSimulcast.TryParse(attrValue);
                break;

            // --- BUNDLE group (RFC 5888) ---
            case "group" when current is null && !string.IsNullOrWhiteSpace(attrValue):
                sessionGroup = attrValue.Trim();
                break;

            // --- ICE credentials (RFC 8839) ---
            case "ice-ufrag":
                if (current is null)
                    sessionIceUfrag = attrValue.Trim();
                else
                    current.IceUfrag = attrValue.Trim();
                break;

            case "ice-pwd":
                if (current is null)
                    sessionIcePwd = attrValue.Trim();
                else
                    current.IcePwd = attrValue.Trim();
                break;

            case "ice-options":
                if (current is null)
                    sessionIceOptions = attrValue.Trim();
                else
                    current.IceOptions = attrValue.Trim();
                break;

            // --- ICE candidate (RFC 8839) ---
            case "candidate" when current is not null:
            {
                var candidate = SdpIceCandidate.TryParse(attrValue);
                if (candidate is not null)
                    current.Candidates.Add(candidate);
                break;
            }

            // --- end-of-candidates (RFC 8840) ---
            case "end-of-candidates" when current is not null:
                current.EndOfCandidates = true;
                break;

            // --- SDES crypto (RFC 4568) ---
            case "crypto" when current is not null:
            {
                var crypto = SdpCryptoAttribute.TryParse(attrValue);
                if (crypto is not null)
                    current.Crypto.Add(crypto);
                break;
            }

            // --- DTLS fingerprint (RFC 8122 / RFC 5763) ---
            case "fingerprint":
            {
                var fp = SdpFingerprint.TryParse(attrValue);
                if (fp is not null)
                {
                    if (current is null)
                        sessionFingerprint = fp;
                    else
                        current.Fingerprint = fp;
                }
                break;
            }

            // --- DTLS setup role (RFC 4145) ---
            case "setup" when !string.IsNullOrWhiteSpace(attrValue):
                if (current is null)
                    sessionDtlsSetup = attrValue.Trim();
                else
                    current.DtlsSetup = attrValue.Trim();
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Media line
    // -------------------------------------------------------------------------

    private static MediaBuilder ParseMediaLine(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            throw new FormatException($"Invalid SDP media line: m={value}");

        if (!int.TryParse(parts[1], out var port))
            throw new FormatException($"Invalid SDP media port: m={value}");

        var payloadTypes = parts
            .Skip(3)
            .Select(v => int.TryParse(v, out var pt) ? pt : -1)
            .Where(v => v >= 0)
            .ToArray();

        var codecs = payloadTypes.ToDictionary(
            pt => pt,
            pt => new SdpCodecDefinition { PayloadType = pt, Name = $"PT{pt}", ClockRate = 8000 });

        return new MediaBuilder(parts[0], port, parts[2], codecs, payloadTypes);
    }

    // -------------------------------------------------------------------------
    // rtpmap
    // -------------------------------------------------------------------------

    private static SdpCodecDefinition? ParseRtpMap(string value)
    {
        // Format: PT encoding-name/clock-rate[/channels]
        var parts = value.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var payloadType))
            return null;

        var codecParts = parts[1].Split('/', StringSplitOptions.TrimEntries);
        if (codecParts.Length < 2 || !int.TryParse(codecParts[1], out var clockRate))
            return null;

        var channels = 1;
        if (codecParts.Length >= 3)
            int.TryParse(codecParts[2], out channels);

        return new SdpCodecDefinition
        {
            PayloadType = payloadType,
            Name = codecParts[0],
            ClockRate = clockRate,
            Channels = channels > 0 ? channels : 1
        };
    }

    // -------------------------------------------------------------------------
    // Address helpers
    // -------------------------------------------------------------------------

    private static string? ParseConnectionAddress(string lineTail)
    {
        // c=IN IP4 <addr>  or  c=IN IP6 <addr>
        var parts = lineTail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static string? ParseAddressTail(string lineTail)
    {
        var parts = lineTail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    // -------------------------------------------------------------------------
    // Bandwidth: b=AS:N  (RFC 4566 §5.8)
    // -------------------------------------------------------------------------

    private static int? ParseBandwidth(string value)
    {
        // value: "AS:64" or "TIAS:64000"
        var colonIndex = value.IndexOf(':');
        if (colonIndex <= 0)
            return null;

        return int.TryParse(value[(colonIndex + 1)..].Trim(), out var kbps) ? kbps : null;
    }

    // -------------------------------------------------------------------------
    // Direction token
    // -------------------------------------------------------------------------

    private static SdpMediaDirection? ParseDirectionToken(string token) =>
        token.ToLowerInvariant() switch
        {
            "sendrecv" => SdpMediaDirection.SendRecv,
            "sendonly" => SdpMediaDirection.SendOnly,
            "recvonly" => SdpMediaDirection.RecvOnly,
            "inactive" => SdpMediaDirection.Inactive,
            _ => null
        };

    // -------------------------------------------------------------------------
    // Mutable media builder used during the parse pass
    // -------------------------------------------------------------------------


}
