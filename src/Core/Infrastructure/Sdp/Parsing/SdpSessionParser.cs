using System.Diagnostics.CodeAnalysis;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

/// <summary>
/// SDP parser covering RFC 4566 (updated by RFC 8866) with extensions for
/// RFC 3264 offer/answer, RFC 4568 (SDES), RFC 5761 (rtcp-mux),
/// RFC 5888 (BUNDLE / MID), and RFC 8839 (ICE).
/// </summary>
internal sealed class SdpSessionParser : ISdpSessionParser
{
    // #160 P2-4: wire domains, not just wire syntax. The RTP payload type field is seven bits
    // (RFC 3550 §5.1) and a transport port is sixteen (RFC 8866 §5.14); a value outside those ranges is
    // not a large value, it is a value that cannot exist on the wire.
    private const int MaxPayloadType = 127;
    private const int MaxPort = 65535;

    private readonly SdpParserLimits _limits;

    /// <summary>Creates a parser with the given wire limits (defaults when omitted).</summary>
    public SdpSessionParser(SdpParserLimits? limits = null)
        => _limits = limits ?? SdpParserLimits.Default;

    /// <inheritdoc />
    public bool TryParse(string? sdp, [NotNullWhen(true)] out SdpSessionDescription? result)
    {
        // Handle the empty-input case here so the catch below need not swallow ArgumentException —
        // that way a genuine argument bug inside Parse still surfaces instead of looking like a parse
        // failure. Only the wire-shaped failures below are treated as a controlled drop.
        if (string.IsNullOrWhiteSpace(sdp))
        {
            result = null;
            return false;
        }

        try
        {
            result = Parse(sdp);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // Untrusted remote input: a malformed or over-limit body is a controlled drop, never a
            // throw out of the parse contract (K4). Call sites treat null as an observable parse failure.
            result = null;
            return false;
        }
    }

    /// <inheritdoc />
    public SdpSessionDescription Parse(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
            throw new ArgumentException("SDP cannot be empty.", nameof(sdp));

        // Bound the whole body before splitting/allocating from attacker-controlled input (K4).
        if (sdp.Length > _limits.MaxSdpBytes)
            throw new FormatException($"SDP exceeds the maximum size of {_limits.MaxSdpBytes} bytes.");

        var lines = sdp.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length > _limits.MaxLines)
            throw new FormatException($"SDP exceeds the maximum of {_limits.MaxLines} lines.");

        string originAddress = "127.0.0.1";
        // No silent loopback default: a session with neither a session-level nor a media-level
        // c= line has no destination and is rejected below (RFC 4566 §5.7), not sent to 127.0.0.1.
        string? sessionConnectionAddress = null;
        var sessionDirection = SdpMediaDirection.SendRecv;
        string? sessionGroup = null;
        string? sessionIceUfrag = null;
        string? sessionIcePwd = null;
        string? sessionIceOptions = null;
        SdpFingerprint? sessionFingerprint = null;
        string? sessionDtlsSetup = null;

        // RFC 4566 §5: v=, s= and t= are mandatory session-description lines.
        var hasVersion = false;
        var hasOrigin = false;
        var hasSessionName = false;
        var hasTiming = false;

        // #160 P2-15: one guard for the session level; each media section carries its own.
        var sessionSingletons = new SdpSingletonGuard();
        var sessionBandwidths = new List<SdpBandwidth>();

        var media = new List<SdpMediaDescription>();
        MediaBuilder? current = null;

        foreach (var line in lines)
        {
            if (line.Length > _limits.MaxLineBytes)
                throw new FormatException($"SDP line exceeds the maximum of {_limits.MaxLineBytes} bytes.");

            if (line.Length < 2 || line[1] != '=')
                continue;

            var type = line[0];
            var value = line[2..];

            switch (type)
            {
                case 'v':
                    hasVersion = true;
                    break;

                case 's':
                    hasSessionName = true;
                    break;

                case 't':
                    hasTiming = true;
                    break;

                case 'o':
                    // #160 P2-12: o= is mandatory and has six fields (RFC 8866 §5.2). Presence alone is
                    // not enough — a truncated o= is what a malformed description looks like.
                    hasOrigin = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6;
                    originAddress = ParseAddressTail(value) ?? originAddress;
                    break;

                case 'c':
                    var addr = ParseConnectionAddress(value);
                    if (!string.IsNullOrWhiteSpace(addr))
                    {
                        if (current is null)
                            sessionConnectionAddress = addr;
                        else
                            current.ConnectionAddress = addr;
                    }
                    break;

                case 'b':
                {
                    // #160 P3-18: sammeln statt überschreiben, und die Session-Ebene nicht verwerfen.
                    // Mehrere b= mit verschiedenen Typ-Tokens (AS, TIAS, RR, RS) beschreiben
                    // Verschiedenes; keines ersetzt das andere (RFC 4566 §5.8).
                    var bandwidth = ParseBandwidth(value);
                    if (bandwidth is not null)
                    {
                        if (current is null)
                            sessionBandwidths.Add(bandwidth);
                        else
                            current.Bandwidths.Add(bandwidth);
                    }
                    break;
                }

                case 'm':
                    if (current is not null)
                        media.Add(current.Build(sessionDirection));
                    if (media.Count >= _limits.MaxMediaSections)
                        throw new FormatException($"SDP exceeds the maximum of {_limits.MaxMediaSections} media sections.");
                    current = ParseMediaLine(value);
                    break;

                case 'a':
                    ParseAttribute(
                        value,
                        current,
                        sessionSingletons,
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
            media.Add(current.Build(sessionDirection));

        // RFC 8866 §5: reject an SDP missing a mandatory v=, o=, s= or t= line rather than
        // accepting a structurally invalid description.
        //
        // #160 P2-12: o= was the one mandatory line not checked. It carries the session id and version
        // that identify a description across re-offers (RFC 3264 §8) — without it, a re-INVITE cannot be
        // told apart from a fresh session, and OriginAddress silently stayed whatever it had defaulted to.
        if (!hasVersion || !hasOrigin || !hasSessionName || !hasTiming)
            throw new FormatException("SDP is missing a mandatory v=, o=, s= or t= line (RFC 8866 §5).");

        // RFC 4566 §5.7: a connection address must be present at the session level or on every
        // media section. Without any valid c=, the media has no destination — reject instead of
        // silently defaulting to loopback (which would send media to 127.0.0.1).
        if (sessionConnectionAddress is null && media.Any(m => m.ConnectionAddress is null))
            throw new FormatException("SDP has no connection address (RFC 4566 §5.7).");

        return new SdpSessionDescription
        {
            OriginAddress = originAddress,
            Bandwidths = sessionBandwidths.AsReadOnly(),
            ConnectionAddress = sessionConnectionAddress ?? string.Empty,
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

    private void ParseAttribute(
        string value,
        MediaBuilder? current,
        SdpSingletonGuard sessionSingletons,
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

        // #160 P2-15: the at-most-once attributes are guarded per level, so the meaning of the
        // description stops depending on the order the peer wrote its lines in.
        var singletons = current?.Singletons ?? sessionSingletons;

        switch (name.ToLowerInvariant())
        {
            // --- direction (RFC 8866 §6.7: at most one per level) ---
            case "sendrecv":
            case "sendonly":
            case "recvonly":
            case "inactive":
            {
                var dir = ParseDirectionToken(name);
                if (dir.HasValue && singletons.Accept("direction", name.ToLowerInvariant()))
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
                {
                    if (!current.Codecs.ContainsKey(codec.PayloadType)
                        && current.Codecs.Count >= _limits.MaxPayloadTypesPerMedia)
                    {
                        throw new FormatException(
                            $"SDP media section exceeds the maximum of {_limits.MaxPayloadTypesPerMedia} payload types.");
                    }

                    current.Codecs[codec.PayloadType] = codec;
                }
                break;
            }

            // --- fmtp ---
            case "fmtp" when current is not null:
            {
                var fmtp = SdpFmtpAttribute.TryParse(attrValue);
                if (fmtp is not null)
                {
                    if (current.Fmtp.Count >= _limits.MaxFmtpPerMedia)
                        throw new FormatException($"SDP media section exceeds the maximum of {_limits.MaxFmtpPerMedia} fmtp attributes.");
                    current.Fmtp.Add(fmtp);
                }
                break;
            }

            // --- RTCP feedback (RFC 4585 §4.2) ---
            case "rtcp-fb" when current is not null:
            {
                var feedback = SdpRtcpFeedback.TryParse(attrValue);
                if (feedback is not null)
                {
                    if (current.RtcpFeedback.Count >= _limits.MaxRtcpFeedbackPerMedia)
                        throw new FormatException($"SDP media section exceeds the maximum of {_limits.MaxRtcpFeedbackPerMedia} rtcp-fb attributes.");
                    current.RtcpFeedback.Add(feedback);
                }
                break;
            }

            // --- RTP header extension mapping (RFC 8285 §5) ---
            case "extmap" when current is not null:
            {
                var extmap = SdpExtmap.TryParse(attrValue);
                if (extmap is not null)
                {
                    if (current.Extensions.Count >= _limits.MaxHeaderExtensionsPerMedia)
                        throw new FormatException($"SDP media section exceeds the maximum of {_limits.MaxHeaderExtensionsPerMedia} extmap attributes.");
                    current.Extensions.Add(extmap);
                }
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

            // RFC 8858: the offerer opened no separate RTCP port. Read as a mux request in its own
            // right — §4 requires it to accompany a=rtcp-mux, and an offer that carries only this one
            // is unambiguous about what it wants (#160 P2-9).
            // RFC 5506: permits an RTCP datagram that is not a full compound. Without it every
            // feedback packet has to be wrapped in SR/RR + SDES (RFC 3550 §6.1) — #162 P2-3.
            case "rtcp-rsize" when current is not null:
                current.ReducedSizeRtcp = true;
                break;

            case "rtcp-mux-only" when current is not null:
                current.RtcpMuxOnly = true;
                current.RtcpMux = true;
                break;

            case "rtcp" when current is not null && int.TryParse(attrValue.Split(' ')[0], out var rtcpPort):
                current.RtcpPort = rtcpPort;
                break;

            // --- MID (RFC 5888) — exactly one per m-line; it is the 1:1 handle offer and answer are
            // matched by (RFC 8829 §5.3.1), so a section with two of them has no identity at all. ---
            case "mid" when current is not null && !string.IsNullOrWhiteSpace(attrValue):
            {
                var mid = attrValue.Trim();
                if (singletons.Accept("mid", mid))
                    current.Mid = mid;
                break;
            }

            // --- MSID (RFC 8830): MediaStream / track identity ---
            case "msid" when current is not null:
                current.Msid = SdpMsid.TryParse(attrValue);
                break;

            // --- Simulcast (RFC 8851 rid / RFC 8853 simulcast) ---
            case "rid" when current is not null:
            {
                var rid = SdpRid.TryParse(attrValue);
                if (rid is not null)
                {
                    if (current.Rids.Count >= _limits.MaxRidsPerMedia)
                        throw new FormatException($"SDP media section exceeds the maximum of {_limits.MaxRidsPerMedia} rid attributes.");
                    current.Rids.Add(rid);
                }
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
            // At most one ufrag/pwd per level (RFC 8839 §5.4). They are the ICE short-term credential:
            // two different values decide which STUN checks authenticate, so a contradiction is fatal
            // rather than resolvable.
            // #160 P2-14: the credential is validated against the grammar (RFC 8839 §5.4), not just
            // stored. A ufrag outside 4..256 ice-chars produces STUN checks whose USERNAME can never
            // match what the peer computes — every check fails and the call never connects, with
            // nothing pointing back at the SDP. Rejecting the description is deliberate: merely
            // dropping the attribute would leave an m-line that looks ICE-less and silently take the
            // non-ICE path, which is a downgrade rather than a failure.
            // #160 P2-14: the credential is validated, not merely stored. A ufrag outside the length
            // bounds produces STUN checks whose USERNAME can never match what the peer computes — every
            // check fails and the call never connects, with nothing pointing back at the SDP.
            // Rejecting the description is deliberate: dropping only the attribute would leave an
            // m-line that looks ICE-less and silently take the non-ICE path, a downgrade rather than a
            // failure. libwebrtc rejects the description here as well; SIPSorcery does not look at all.
            case "ice-ufrag":
            {
                var ufrag = attrValue.Trim();
                if (!SdpIceGrammar.IsValidUfrag(ufrag))
                    throw new FormatException("SDP carries an unusable ice-ufrag (RFC 8839 §5.4).");

                if (singletons.Accept("ice-ufrag", ufrag))
                {
                    if (current is null)
                        sessionIceUfrag = ufrag;
                    else
                        current.IceUfrag = ufrag;
                }
                break;
            }

            case "ice-pwd":
            {
                var pwd = attrValue.Trim();
                // The 22-character floor is what gives the short-term credential its entropy; a shorter
                // one is guessable, not merely unusual. Neither the value nor its length is logged (K5).
                if (!SdpIceGrammar.IsValidPassword(pwd))
                    throw new FormatException("SDP carries an ice-pwd outside the RFC 8839 §5.4 grammar.");

                if (singletons.Accept("ice-pwd", pwd))
                {
                    if (current is null)
                        sessionIcePwd = pwd;
                    else
                        current.IcePwd = pwd;
                }
                break;
            }

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
                {
                    if (current.Candidates.Count >= _limits.MaxIceCandidatesPerMedia)
                        throw new FormatException($"SDP media section exceeds the maximum of {_limits.MaxIceCandidatesPerMedia} candidate attributes.");
                    current.Candidates.Add(candidate);
                }
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
                {
                    if (current.Crypto.Count >= _limits.MaxCryptoPerMedia)
                        throw new FormatException($"SDP media section exceeds the maximum of {_limits.MaxCryptoPerMedia} crypto attributes.");
                    current.Crypto.Add(crypto);
                }
                break;
            }

            // --- DTLS fingerprint (RFC 8122 / RFC 5763) ---
            // Several fingerprint lines are legal when they name DIFFERENT hash functions — the same
            // certificate measured more than one way (RFC 8122 §5). Two lines for the SAME function with
            // different digests is a contradiction, and it is the one that matters: the fingerprint is
            // the only thing authenticating the DTLS peer. Across different functions the FIRST is kept,
            // so which certificate we will accept does not depend on line order either.
            case "fingerprint":
            {
                var fp = SdpFingerprint.TryParse(attrValue);
                if (fp is not null
                    && singletons.Accept($"fingerprint:{fp.Algorithm.ToLowerInvariant()}", fp.Value))
                {
                    if (current is null)
                        sessionFingerprint ??= fp;
                    else
                        current.Fingerprint ??= fp;
                }
                break;
            }

            // --- DTLS setup role (RFC 4145) ---
            // #160 P2-5: only the four roles the grammar defines. An unrecognised value used to be stored
            // verbatim and then treated as "some role was signalled", so "a=setup:nonsense" produced an
            // active DTLS m-line whose role nobody had actually agreed. Dropping it leaves the role unset,
            // which the DTLS layer already fails closed on.
            case "setup" when IsKnownSetupRole(attrValue):
            {
                // At most one per level (RFC 4145 §4). "passive" then "active" decides who runs the
                // DTLS handshake as client — a question two peers must not answer differently.
                var role = attrValue.Trim();
                if (singletons.Accept("setup", role.ToLowerInvariant()))
                {
                    if (current is null)
                        sessionDtlsSetup = role;
                    else
                        current.DtlsSetup = role;
                }
                break;
            }
        }
    }

    // RFC 4145 §4 defines exactly these four roles; anything else is not a role we can act on.
    private static bool IsKnownSetupRole(string value)
        => value.Trim() is var role
           && (role.Equals("active", StringComparison.OrdinalIgnoreCase)
               || role.Equals("passive", StringComparison.OrdinalIgnoreCase)
               || role.Equals("actpass", StringComparison.OrdinalIgnoreCase)
               || role.Equals("holdconn", StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------------
    // Media line
    // -------------------------------------------------------------------------

    private MediaBuilder ParseMediaLine(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            throw new FormatException($"Invalid SDP media line: m={value}");

        var (port, portCount) = ParseMediaPort(parts[1], value);

        var profile = parts[2];
        var formats = parts.Skip(3).ToArray();

        // #160 P2-11: the fmt field is a payload-type list only under an RTP profile. Under any other
        // one it is opaque (RFC 8866 §5.14) — "UDP/DTLS/SCTP webrtc-datachannel" names a protocol.
        // Parsing that as an integer failed silently and left the section with no format at all.
        var codecs = IsRtpProfile(profile)
            ? ParsePayloadTypes(formats, value, out var mLineOrder)
            : Empty(out mLineOrder);

        return new MediaBuilder(parts[0], port, profile, codecs, mLineOrder)
        {
            PortCount = portCount,
            Formats = formats,
        };

        static Dictionary<int, SdpCodecDefinition> Empty(out IReadOnlyList<int> order)
        {
            order = [];
            return [];
        }
    }

    /// <summary>
    /// Parses the <c>&lt;port&gt;[/&lt;number of ports&gt;]</c> field of an m-line (RFC 8866 §5.14).
    /// </summary>
    private static (int Port, int PortCount) ParseMediaPort(string field, string mediaLine)
    {
        // #160 P2-11: the "/n" suffix is legal SDP, and rejecting it failed the entire description
        // rather than the one field — a peer offering "m=video 40000/2 RTP/AVP 96" got nothing back.
        var portText = field;
        var count = 1;

        var slash = field.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            portText = field[..slash];
            var countText = field[(slash + 1)..];
            if (!int.TryParse(countText, out count) || count < 1)
                throw new FormatException($"Invalid SDP media port count: m={mediaLine}");
        }

        // #160 P2-4: validate the numeric wire domains rather than accepting any int. A port outside
        // 0..65535 cannot be bound, and the value would be carried around until something downstream
        // truncated it (RFC 8866 §5.14).
        if (!int.TryParse(portText, out var port) || port is < 0 or > MaxPort)
            throw new FormatException($"Invalid SDP media port: m={mediaLine}");

        // The range has to fit the 16-bit port space too: "65535/4" describes ports that cannot exist.
        if (port + count - 1 > MaxPort)
            throw new FormatException($"SDP media port range exceeds the 16-bit port space: m={mediaLine}");

        return (port, count);
    }

    // RFC 8866 §5.14: only an RTP-based profile gives the fmt field payload-type semantics.
    private static bool IsRtpProfile(string profile) =>
        profile.Contains("RTP/", StringComparison.OrdinalIgnoreCase);

    private Dictionary<int, SdpCodecDefinition> ParsePayloadTypes(
        string[] formats,
        string mediaLine,
        out IReadOnlyList<int> mLineOrder)
    {
        // The RTP payload type field is seven bits (RFC 3550 §5.1), so 0..127. Accepting 256 here meant
        // answering "RTP/AVP 256" and then casting it to byte further down the pipeline, where it silently
        // became 0 — PCMU — a payload type nobody negotiated.
        var payloadTypes = formats
            .Select(v => int.TryParse(v, out var pt) && pt is >= 0 and <= MaxPayloadType ? pt : -1)
            .Where(v => v >= 0)
            .ToArray();

        // #160 P1-1 (part 2): bound the payload-type list a single m= line can declare (K4).
        if (payloadTypes.Length > _limits.MaxPayloadTypesPerMedia)
            throw new FormatException($"SDP media line exceeds the maximum of {_limits.MaxPayloadTypesPerMedia} payload types: m={mediaLine}");

        // Build the payload-type map with TryAdd rather than ToDictionary: a duplicate PT on the
        // m-line (e.g. "RTP/AVP 0 0") would make ToDictionary throw ArgumentException, escaping the
        // FormatException/null Try* contract. Reject it as a controlled parse failure instead (K4).
        var codecs = new Dictionary<int, SdpCodecDefinition>(payloadTypes.Length);
        foreach (var pt in payloadTypes)
        {
            if (!codecs.TryAdd(pt, new SdpCodecDefinition { PayloadType = pt, Name = $"PT{pt}", ClockRate = 8000 }))
                throw new FormatException($"Duplicate payload type {pt} on SDP media line: m={mediaLine}");
        }

        mLineOrder = payloadTypes;
        return codecs;
    }

    // -------------------------------------------------------------------------
    // rtpmap
    // -------------------------------------------------------------------------

    private static SdpCodecDefinition? ParseRtpMap(string value)
    {
        // Format: PT encoding-name/clock-rate[/channels]
        var parts = value.Split(' ', 2, StringSplitOptions.TrimEntries);
        // #160 P2-4: same seven-bit domain as the m-line. An rtpmap for a payload type the m-line could
        // never carry describes a format that cannot be signalled.
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var payloadType)
            || payloadType is < 0 or > MaxPayloadType)
        {
            return null;
        }

        var codecParts = parts[1].Split('/', StringSplitOptions.TrimEntries);
        if (codecParts.Length < 2 || !int.TryParse(codecParts[1], out var clockRate) || clockRate <= 0)
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
    // Bandwidth: b=<bwtype>:<value>  (RFC 4566 §5.8, e.g. AS in kbit/s, TIAS in bit/s)
    // -------------------------------------------------------------------------

    private static SdpBandwidth? ParseBandwidth(string value)
    {
        // value: "AS:64" or "TIAS:64000" — the type token must round-trip so AS (kbit/s) and
        // TIAS (bit/s, RFC 3890) are not conflated (a factor-1000 error otherwise).
        var colonIndex = value.IndexOf(':');
        if (colonIndex <= 0)
            return null;

        var type = value[..colonIndex].Trim();
        if (type.Length == 0
            || !int.TryParse(value[(colonIndex + 1)..].Trim(), out var bandwidth))
        {
            return null;
        }

        return new SdpBandwidth { Type = type, Value = bandwidth };
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
}
