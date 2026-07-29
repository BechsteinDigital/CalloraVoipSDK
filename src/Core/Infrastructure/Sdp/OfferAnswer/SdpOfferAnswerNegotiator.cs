using System.Globalization;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Negotiates SDP offers and answers per RFC 3264.
/// Handles codec intersection, direction resolution, fmtp carry-through,
/// ptime reflection, and telephone-event (RFC 4733) inclusion.
/// Also carries through: rtcp-mux (RFC 5761), BUNDLE/MID (RFC 5888),
/// SDES crypto (RFC 4568), and DTLS fingerprint/setup (RFC 5763 / RFC 4145).
/// </summary>
internal sealed class SdpOfferAnswerNegotiator : ISdpOfferAnswerNegotiator
{
    // Highest IANA statically-assigned RTP payload type (RFC 3551 §6 — 0–34 are static;
    // 96–127 are dynamic and require an rtpmap to carry meaning).
    private const int MaxStaticPayloadType = 34;


    /// <inheritdoc />
    public SdpSessionDescription CreateOffer(
        IPEndPoint localEndPoint,
        IReadOnlyList<SdpCodecDefinition> codecs,
        SdpMediaDirection direction,
        SdpMediaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(codecs);

        var host = LocalEndPointHostResolver.ResolveHost(localEndPoint);
        var dtls = options?.Dtls;
        var ice = options?.Ice;
        var bundle = options?.Bundle == true;
        var rtcpMux = options?.RtcpMux == true;

        // Multi-track offer (RFC 8843 BUNDLE): one m-line per supplied track with a numeric a=mid by index
        // (0, 1, 2, …, mirroring libwebrtc/SIPSorcery) over one shared transport port. Chosen only when the
        // caller supplies an explicit track list; the fixed single-audio path below stays byte-identical, so
        // the SIP path and existing 1+1 WebRTC offers are unchanged.
        if (options?.Tracks is { Count: > 0 } tracks)
            return BuildMultiTrackOffer(tracks, localEndPoint, host, direction, bundle, rtcpMux, dtls, ice, options);

        var crypto = options?.Crypto ?? [];

        // Profile selection: DTLS wins (RFC 5763, UDP/TLS/RTP/SAVPF); otherwise SDES
        // a=crypto lines key an RTP/SAVP profile (RFC 4568); otherwise plain RTP/AVP.
        var profile = ResolveOfferProfile(dtls, crypto.Count > 0);

        // Video (WebRTC phase 2): a second m-line when requested. SDES keying is per-m-line
        // (RFC 4568): the video m-line carries its own a=crypto (options.Video.Crypto), keyed
        // independently of audio, on the same secure profile.
        var offerVideo = options?.Video is not null;

        // BUNDLE: session-level group + media-level mid (the fixed path keeps the historic semantic mids).
        string? group = null;
        string? mid = null;
        if (bundle)
        {
            group = offerVideo ? "BUNDLE audio video" : "BUNDLE audio";
            mid = "audio";
        }

        var mediaLines = new List<SdpMediaDescription>
        {
            BuildAudioOfferMedia(
                codecs, localEndPoint.Port, profile, direction, mid, options?.AudioMsid, crypto,
                headerExtUris: [], bundle, rtcpMux, dtls, ice, ice?.Candidates ?? [])
        };
        if (offerVideo)
        {
            var video = options!.Video!;
            mediaLines.Add(BuildVideoOfferMedia(
                video.Codecs, video.SimulcastSendRids, video.Port, profile, direction,
                bundle ? "video" : null, options.VideoMsid, video.Crypto, video.HeaderExtensionUris,
                bundle, rtcpMux, dtls, ice, video.Candidates));
        }

        return new SdpSessionDescription
        {
            OriginAddress = host,
            ConnectionAddress = host,
            SessionDirection = direction,
            Group = group,
            Media = mediaLines,
            SessionId = options?.SessionId ?? 0,
            SessionVersion = options?.SessionVersion ?? 0
        };
    }

    // Builds a multi-track offer (RFC 8843 §7): one m-line per track, numeric a=mid by list index, all sharing
    // the one bound transport port under BUNDLE. The group lists the mids in m-line order. Reuses the same
    // per-m-line builders as the fixed path so a track's audio/video m-line is byte-for-byte the shape the 1+1
    // path emits — only the mid (numeric) and the group differ.
    private static SdpSessionDescription BuildMultiTrackOffer(
        IReadOnlyList<SdpTrackOptions> tracks,
        IPEndPoint localEndPoint,
        string host,
        SdpMediaDirection direction,
        bool bundle,
        bool rtcpMux,
        SdpDtlsParameters? dtls,
        SdpIceParameters? ice,
        SdpMediaOptions options)
    {
        // One shared profile for every m-line (DTLS wins; else SDES if any track keys with a=crypto; else plain).
        var profile = ResolveOfferProfile(dtls, tracks.Any(t => t.Crypto.Count > 0));
        // All BUNDLE m-lines share the one bound transport port and the session ICE candidates (RFC 8843).
        var sharedCandidates = ice?.Candidates ?? [];

        var mediaLines = new List<SdpMediaDescription>(tracks.Count);
        var mids = new List<string>(tracks.Count);
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var trackMid = index.ToString(CultureInfo.InvariantCulture);
            mids.Add(trackMid);

            // Per-track direction (RFC 3264): a track that set its own direction emits it; the default is
            // SendRecv, so a list that leaves it unset stays byte-identical to the pre-direction multi-track path.
            mediaLines.Add(track.Kind.Equals("video", StringComparison.OrdinalIgnoreCase)
                ? BuildVideoOfferMedia(
                    track.Codecs, track.SimulcastSendRids, localEndPoint.Port, profile, track.Direction,
                    trackMid, track.Msid, track.Crypto, track.HeaderExtensionUris,
                    bundle, rtcpMux, dtls, ice, sharedCandidates)
                : BuildAudioOfferMedia(
                    track.Codecs, localEndPoint.Port, profile, track.Direction,
                    trackMid, track.Msid, track.Crypto, track.HeaderExtensionUris,
                    bundle, rtcpMux, dtls, ice, sharedCandidates));
        }

        return new SdpSessionDescription
        {
            OriginAddress = host,
            ConnectionAddress = host,
            SessionDirection = direction,
            Group = bundle ? "BUNDLE " + string.Join(' ', mids) : null,
            Media = mediaLines,
            SessionId = options.SessionId,
            SessionVersion = options.SessionVersion
        };
    }

    // Profile selection shared by the fixed and multi-track offer paths: DTLS wins (RFC 5763,
    // UDP/TLS/RTP/SAVPF); otherwise SDES a=crypto keys an RTP/SAVP profile (RFC 4568); otherwise plain RTP/AVP.
    private static string ResolveOfferProfile(SdpDtlsParameters? dtls, bool hasSdesCrypto) =>
        dtls is not null ? "UDP/TLS/RTP/SAVPF" : hasSdesCrypto ? "RTP/SAVP" : "RTP/AVP";

    // The MID SDES header extension (RFC 9143 / RFC 8843 §9) rides every bundled m-line so the peer stamps
    // each packet's MID on the shared transport. It carries the SAME extmap id on every m-line (the
    // demultiplexer reads one id) — offered first so BuildOfferExtmaps assigns it id 1. Outside BUNDLE the
    // extmaps are unchanged.
    private static IReadOnlyList<string> BundledOfferExtmapUris(bool bundle, IReadOnlyList<string> uris) =>
        bundle ? [RtpHeaderExtensionUris.Mid, .. uris] : uris;

    // Builds one audio offer m-line: the given codecs plus telephone-event fmtp, per-m-line SDES crypto, the
    // negotiated header extensions (MID first under BUNDLE), and the session-level DTLS/ICE. Shared by the
    // fixed single-audio path and the multi-track path so both emit byte-identical audio m-lines.
    private static SdpMediaDescription BuildAudioOfferMedia(
        IReadOnlyList<SdpCodecDefinition> codecs, int port, string profile, SdpMediaDirection direction,
        string? mid, SdpMsid? msid, IReadOnlyList<SdpCryptoAttribute> crypto, IReadOnlyList<string> headerExtUris,
        bool bundle, bool rtcpMux, SdpDtlsParameters? dtls, SdpIceParameters? ice,
        IReadOnlyList<SdpIceCandidate> candidates) =>
        new()
        {
            MediaType = "audio",
            Port = port,
            Profile = profile,
            Direction = direction,
            Codecs = codecs,
            Fmtp = BuildFmtpForCodecs(codecs),
            Mid = mid,
            Msid = msid,
            Crypto = crypto,
            Extensions = BuildOfferExtmaps(BundledOfferExtmapUris(bundle, headerExtUris)),
            RtcpMux = rtcpMux,
            IceUfrag = ice?.Ufrag,
            IcePwd = ice?.Pwd,
            IceOptions = ice?.Options,
            Candidates = candidates,
            Fingerprint = dtls is not null
                ? new SdpFingerprint { Algorithm = dtls.Algorithm, Value = dtls.Fingerprint }
                : null,
            DtlsSetup = dtls?.Setup
        };

    // Builds one video offer m-line (WebRTC phase 2): codecs plus RTX repair streams (RFC 4588 §8.1),
    // standard rtcp-fb, send-side simulcast rids (RFC 8853) with the RID header extension (RFC 8852) offered
    // before the app's extensions (MID first under BUNDLE), and session-level DTLS/ICE. Shared by the fixed
    // and multi-track paths.
    private static SdpMediaDescription BuildVideoOfferMedia(
        IReadOnlyList<SdpCodecDefinition> codecs, IReadOnlyList<string> simulcastSendRids, int port,
        string profile, SdpMediaDirection direction, string? mid, SdpMsid? msid,
        IReadOnlyList<SdpCryptoAttribute> crypto, IReadOnlyList<string> headerExtUris,
        bool bundle, bool rtcpMux, SdpDtlsParameters? dtls, SdpIceParameters? ice,
        IReadOnlyList<SdpIceCandidate> candidates)
    {
        var (rtxCodecs, rtxFmtp) = VideoCodecCatalog.BuildRtx(codecs);
        var videoExtmapUris = simulcastSendRids.Count > 0
            ? BundledOfferExtmapUris(bundle, [RtpHeaderExtensionUris.Rid, .. headerExtUris])
            : BundledOfferExtmapUris(bundle, headerExtUris);
        var (rids, simulcastDeclaration) = BuildSimulcast(simulcastSendRids, codecs);

        return new SdpMediaDescription
        {
            MediaType = "video",
            Port = port,
            Profile = profile,
            Direction = direction,
            Codecs = [.. codecs, .. rtxCodecs],
            Fmtp = [.. VideoCodecCatalog.BuildFmtp(codecs), .. rtxFmtp],
            RtcpFeedback = VideoCodecCatalog.StandardFeedback,
            Mid = mid,
            Msid = msid,
            RtcpMux = rtcpMux,
            Crypto = crypto,
            IceUfrag = ice?.Ufrag,
            IcePwd = ice?.Pwd,
            IceOptions = ice?.Options,
            Candidates = candidates,
            Extensions = BuildOfferExtmaps(videoExtmapUris),
            Rids = rids,
            Simulcast = simulcastDeclaration,
            Fingerprint = dtls is not null
                ? new SdpFingerprint { Algorithm = dtls.Algorithm, Value = dtls.Fingerprint }
                : null,
            DtlsSetup = dtls?.Setup
        };
    }

    /// <inheritdoc />
    public SdpOfferAnswerResult NegotiateAnswer(
        SdpSessionDescription remoteOffer,
        IPEndPoint localEndPoint,
        IReadOnlyList<SdpCodecDefinition> localCapabilities,
        SdpMediaDirection localDirection,
        SdpMediaOptions? localOptions = null)
    {
        ArgumentNullException.ThrowIfNull(remoteOffer);
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(localCapabilities);

        var offeredAudio = remoteOffer.Media
            .FirstOrDefault(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));

        if (offeredAudio is null)
            return new SdpOfferAnswerResult { Success = false };

        // Reject disabled m-line (RFC 8866 zero-port) with a mirrored disabled answer.
        if (offeredAudio.Disabled)
        {
            return new SdpOfferAnswerResult
            {
                Success = true,
                Answer = BuildDisabledAnswer(remoteOffer, localEndPoint, localOptions),
                NegotiatedCodecs = []
            };
        }

        // Primary audio answer (RFC 3264 §6): the first audio m-line drives the session result (negotiated
        // codecs and resolved keying) and MUST be answerable — an answer without a keyed audio m-line is
        // rejected (488). Extracted so every further audio m-line under BUNDLE negotiates and keys identically.
        var primaryAudio = TryNegotiateAudioAnswer(
            offeredAudio, remoteOffer, localOptions, localCapabilities, localDirection, localEndPoint);
        if (primaryAudio is null)
            return new SdpOfferAnswerResult { Success = false };

        var host = LocalEndPointHostResolver.ResolveHost(localEndPoint);
        var answerDirection = primaryAudio.Media.Direction;

        // --- BUNDLE/MID (RFC 5888): the answer group is carried only for a BUNDLE offer whose audio has a mid ---
        string? group = null;
        if (offeredAudio.Mid is not null && remoteOffer.Group is not null
            && remoteOffer.Group.StartsWith("BUNDLE", StringComparison.OrdinalIgnoreCase))
        {
            group = remoteOffer.Group;
        }

        // RFC 3264 §6: one answer m-line per offered m-line, in offer order (mid preserved 1:1, RFC 8829
        // §5.3.1). Multi-track (RFC 8843): under BUNDLE every audio and video m-line is answered — they all
        // share the one local port. Without BUNDLE only the first of each media type is answered; a second
        // same-type m-line would need its own local port, so it is declined with a zero-port mirror.
        var isBundle = group is not null;
        var answerLines = new List<SdpMediaDescription>(remoteOffer.Media.Count);
        var videoAnswered = false;
        foreach (var offered in remoteOffer.Media)
        {
            if (ReferenceEquals(offered, offeredAudio))
            {
                answerLines.Add(primaryAudio.Media);
                continue;
            }

            if (offered.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase))
            {
                // A further audio m-line beyond the primary: negotiated only under BUNDLE (shared port).
                var extraAudio = isBundle
                    ? TryNegotiateAudioAnswer(offered, remoteOffer, localOptions, localCapabilities, localDirection, localEndPoint)
                    : null;
                answerLines.Add(extraAudio?.Media ?? BuildDisabledMirror(offered));
                continue;
            }

            // Video: the first, or any under BUNDLE. A second video without BUNDLE would share the single
            // local video port and break demux.
            var videoAnswer = videoAnswered && !isBundle
                ? null
                : TryNegotiateVideoAnswerMedia(offered, remoteOffer, localOptions, answerDirection);
            videoAnswered |= videoAnswer is not null;
            answerLines.Add(videoAnswer ?? BuildDisabledMirror(offered));
        }

        // BUNDLE (RFC 9143 §7.3.3): the answer group lists only accepted mids —
        // rejected m-lines must leave the group.
        if (group is not null)
        {
            var acceptedMids = answerLines
                .Where(m => m.Port > 0 && m.Mid is not null)
                .Select(m => m.Mid)
                .ToArray();
            group = acceptedMids.Length > 0 ? "BUNDLE " + string.Join(' ', acceptedMids) : null;
        }

        var answer = new SdpSessionDescription
        {
            OriginAddress = host,
            ConnectionAddress = host,
            SessionDirection = answerDirection,
            Group = group,
            Media = answerLines,
            SessionId = localOptions?.SessionId ?? 0,
            SessionVersion = localOptions?.SessionVersion ?? 0
        };

        return new SdpOfferAnswerResult
        {
            Success = true,
            Answer = answer,
            NegotiatedCodecs = primaryAudio.NegotiatedCodecs,
            RtcpMuxNegotiated = primaryAudio.RtcpMuxNegotiated,
            RemoteFingerprint = primaryAudio.RemoteFingerprint,
            RemoteDtlsSetup = primaryAudio.RemoteDtlsSetup,
            NegotiatedCrypto = primaryAudio.NegotiatedCrypto,
            LocalCrypto = primaryAudio.LocalCrypto
        };
    }

    // Negotiates the answer m-line for one offered audio m-line (RFC 3264 §6): codec intersection, rtcp-mux
    // confirm (RFC 5761), per-m-line SDES/DTLS keying with fail-closed (RFC 4568/5763), ptime/fmtp carry, and
    // the MID extension echo (RFC 9143). Returns null — a decline — when the m-line is not answerable audio:
    // disabled/zero-port, no real (non-telephone-event) codec, or a secure profile keyable neither via SDES
    // (no answerable a=crypto) nor via DTLS (no fingerprint / local identity). Shared by the primary audio and,
    // under BUNDLE, every further audio m-line, so multi-track audio answers key identically to the 1+1 path.
    private static AudioAnswerNegotiation? TryNegotiateAudioAnswer(
        SdpMediaDescription offered,
        SdpSessionDescription remoteOffer,
        SdpMediaOptions? localOptions,
        IReadOnlyList<SdpCodecDefinition> localCapabilities,
        SdpMediaDirection localDirection,
        IPEndPoint localEndPoint)
    {
        if (!offered.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase)
            || offered.Disabled || offered.Port <= 0)
        {
            return null;
        }

        var negotiated = NegotiateCodecs(offered.Codecs, localCapabilities);

        // At least one real audio codec — an answer of only telephone-event would be an audio-less call.
        if (!negotiated.Any(c => !c.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase)))
            return null;

        var answerDirection = ResolveAnswerDirection(offered.Direction, localDirection);

        // Carry fmtp from the offer for accepted payload types, reflect ptime, confirm rtcp-mux only when
        // offered (RFC 3264 §6.1 / RFC 5761 §5.1.1 — the answer cannot enable mux the offer did not advertise).
        var acceptedPts = new HashSet<int>(negotiated.Select(c => c.PayloadType));
        var carriedFmtp = offered.Fmtp.Where(f => acceptedPts.Contains(f.PayloadType)).ToArray();
        var ptime = offered.Ptime;
        var rtcpMux = offered.RtcpMux;

        // SDES crypto (RFC 4568 §5.1.3): answer the first supported suite with our OWN key. Ignored on a
        // DTLS-keyed profile (fingerprint-keyed; any a=crypto on UDP/TLS/* must be ignored, RFC 5763 / HARD-S1).
        IReadOnlyList<SdpCryptoAttribute> crypto = [];
        SdpCryptoAttribute? localCrypto = null;
        SdpCryptoAttribute? remoteCrypto = null;
        if (offered.Crypto.Count > 0 && !SdpSecurityInspector.IsDtlsProfile(offered.Profile))
        {
            var sdes = SdesCryptoSelector.SelectAnswer(offered.Crypto);
            if (sdes is not null)
            {
                localCrypto = sdes.LocalAnswer;
                remoteCrypto = sdes.RemoteOffer;
                crypto = [localCrypto];
            }
        }

        // DTLS (RFC 5763): fingerprint answer only when the peer offered one and SDES did not win — the keying
        // methods are mutually exclusive per m-line. The fingerprint decides, not the profile (§6.6).
        SdpFingerprint? fingerprint = null;
        string? dtlsSetup = null;
        var remoteFp = offered.Fingerprint ?? remoteOffer.Fingerprint;
        var remoteSetup = offered.DtlsSetup ?? remoteOffer.DtlsSetup;
        if (localOptions?.Dtls is not null && remoteFp is not null && localCrypto is null)
        {
            fingerprint = new SdpFingerprint
            {
                Algorithm = localOptions.Dtls.Algorithm,
                Value = localOptions.Dtls.Fingerprint
            };
            dtlsSetup = ResolveAnswerSetup(remoteSetup);
        }

        // Fail closed: a secure-profile offer keyable neither via SDES nor DTLS is declined, never answered in
        // the clear (RFC 3264 §5.1). A DTLS-keyed profile additionally requires a DTLS answer.
        if (localCrypto is null && fingerprint is null && IsSdesSecuredProfile(offered.Profile))
            return null;
        if (fingerprint is null && SdpSecurityInspector.IsDtlsProfile(offered.Profile))
            return null;

        var ice = localOptions?.Ice;
        var media = new SdpMediaDescription
        {
            MediaType = "audio",
            Port = localEndPoint.Port,
            Profile = ResolveAnswerProfile(offered.Profile),
            Codecs = negotiated,
            Direction = answerDirection,
            Fmtp = carriedFmtp,
            Ptime = ptime,
            Mid = offered.Mid,
            // Multi-track (RFC 8843): each answered audio m-line takes its own a=msid (RFC 8830) keyed by
            // the offered MID; absent from the map (or single-audio) it falls back to the one AudioMsid, so
            // the 1+1 answer path is byte-identical.
            Msid = ResolveAnswerAudioMsid(localOptions, offered.Mid),
            RtcpMux = rtcpMux,
            Crypto = crypto,
            Fingerprint = fingerprint,
            DtlsSetup = dtlsSetup,
            IceUfrag = ice?.Ufrag,
            IcePwd = ice?.Pwd,
            IceOptions = ice?.Options,
            Candidates = ice?.Candidates ?? [],
            // Echo the MID SDES extension (RFC 9143) when the BUNDLE offer advertised it (no-op otherwise).
            Extensions = BuildAnswerExtmaps(offered.Extensions, WithMidExtension([]))
        };

        return new AudioAnswerNegotiation(media, negotiated, rtcpMux, remoteFp, remoteSetup, remoteCrypto, localCrypto);
    }

    // Resolves the a=msid (RFC 8830) for one answered audio m-line. A multi-track answer (RFC 8843) names
    // the msid per offered MID via AudioMsidByMid so an SFU forwards N distinct participant audios; a MID the
    // map does not name — and every m-line on the single-audio path, which supplies only AudioMsid — falls
    // back to the one AudioMsid, keeping the 1+1 answer byte-identical.
    private static SdpMsid? ResolveAnswerAudioMsid(SdpMediaOptions? localOptions, string? offeredMid)
    {
        if (localOptions is null)
            return null;

        if (offeredMid is not null
            && localOptions.AudioMsidByMid is { } byMid
            && byMid.TryGetValue(offeredMid, out var perLineMsid))
        {
            return perLineMsid;
        }

        return localOptions.AudioMsid;
    }

    // -------------------------------------------------------------------------
    // Codec intersection
    // -------------------------------------------------------------------------

    private static List<SdpCodecDefinition> NegotiateCodecs(
        IReadOnlyList<SdpCodecDefinition> offered,
        IReadOnlyList<SdpCodecDefinition> localCapabilities)
    {
        var localByIdentity = localCapabilities.ToDictionary(
            c => BuildCodecIdentity(c),
            c => c);

        var negotiated = new List<SdpCodecDefinition>();

        foreach (var offer in offered)
        {
            var identity = BuildCodecIdentity(offer);

            // Telephone-event: accept any offered PT if we support it locally by name.
            if (offer.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase))
            {
                if (localByIdentity.ContainsKey(identity))
                {
                    negotiated.Add(new SdpCodecDefinition
                    {
                        PayloadType = offer.PayloadType,
                        Name = offer.Name,
                        ClockRate = offer.ClockRate
                    });
                }
                continue;
            }

            if (!localByIdentity.TryGetValue(identity, out var local))
                continue;

            negotiated.Add(new SdpCodecDefinition
            {
                PayloadType = offer.PayloadType,
                Name = local.Name,
                ClockRate = local.ClockRate,
                Channels = local.Channels
            });
        }

        // Fallback: static payload type intersection for codecs without rtpmap. Restricted to the
        // IANA statically-assigned range (0–34, RFC 3551): those numbers imply a fixed codec even
        // without an rtpmap. Dynamic payload types (96–127) carry NO implied meaning — a bare PT
        // match there could bind a codec the peer never offered, so they must have matched by name
        // above (ResolveEffectiveName already maps 0/8/9) and are never taken by this fallback.
        if (negotiated.Count == 0)
        {
            var localByPt = localCapabilities.ToDictionary(c => c.PayloadType);
            foreach (var offer in offered)
            {
                if (offer.PayloadType <= MaxStaticPayloadType
                    && localByPt.TryGetValue(offer.PayloadType, out var local))
                    negotiated.Add(new SdpCodecDefinition
                    {
                        PayloadType = offer.PayloadType,
                        Name = local.Name,
                        ClockRate = local.ClockRate,
                        Channels = local.Channels
                    });
            }
        }

        return negotiated;
    }

    private static string BuildCodecIdentity(SdpCodecDefinition codec)
    {
        var channels = codec.Channels > 1 ? $"/{codec.Channels}" : string.Empty;
        return $"{ResolveEffectiveName(codec)}:{codec.ClockRate}{channels}";
    }

    /// <summary>
    /// Resolves the effective encoding name for identity matching. Offers may list static
    /// payload types (RFC 3551) on the m-line without an rtpmap line — the parser then
    /// names them "PT&lt;n&gt;". Those must still match our named capabilities, otherwise
    /// an answer to e.g. a Fritz!Box offer (m=audio ... 9 8 0 101, rtpmap only for 101)
    /// contains no audio codec at all and the peer drops the call with 488.
    /// </summary>
    private static string ResolveEffectiveName(SdpCodecDefinition codec)
    {
        var name = codec.Name.ToUpperInvariant();
        if (!name.StartsWith("PT", StringComparison.Ordinal))
            return name;

        return codec.PayloadType switch
        {
            0 => "PCMU",
            8 => "PCMA",
            9 => "G722",
            _ => name
        };
    }

    // -------------------------------------------------------------------------
    // fmtp for offer
    // -------------------------------------------------------------------------

    private static IReadOnlyList<SdpFmtpAttribute> BuildFmtpForCodecs(IReadOnlyList<SdpCodecDefinition> codecs)
    {
        var result = new List<SdpFmtpAttribute>();
        foreach (var codec in codecs)
        {
            if (codec.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase))
                result.Add(new SdpFmtpAttribute { PayloadType = codec.PayloadType, Parameters = "0-16" });
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Video answer (WebRTC phase 2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Negotiates the answer m-line for one offered video m-line (RFC 3264 §6 + RFC 6184/
    /// 7741 codecs at 90 kHz). SDES-keyed video (RFC 4568) is answered with our own key for
    /// the video m-line, mirroring the audio path; DTLS-keyed video is answered with a
    /// fingerprint. Returns <see langword="null"/> — a zero-port decline — when video is not
    /// enabled locally, no codec matches, or a secure video m-line can be keyed neither via
    /// SDES (no answerable a=crypto) nor via DTLS (no fingerprint / local identity).
    /// </summary>
    private static SdpMediaDescription? TryNegotiateVideoAnswerMedia(
        SdpMediaDescription offered,
        SdpSessionDescription remoteOffer,
        SdpMediaOptions? localOptions,
        SdpMediaDirection answerDirection)
    {
        if (localOptions?.Video is not { } video
            || !offered.MediaType.Equals("video", StringComparison.OrdinalIgnoreCase)
            || offered.Disabled
            || offered.Port <= 0)
        {
            return null;
        }

        var remoteFp = offered.Fingerprint ?? remoteOffer.Fingerprint;

        // SDES crypto (RFC 4568): answer the first supported suite with our OWN key for the
        // video m-line. Only on a non-DTLS profile — a DTLS profile (UDP/TLS/*) is fingerprint-
        // keyed and any a=crypto on it is ignored (RFC 5763); the two keying methods are
        // mutually exclusive per m-line.
        IReadOnlyList<SdpCryptoAttribute> videoCrypto = [];
        if (offered.Crypto.Count > 0 && !SdpSecurityInspector.IsDtlsProfile(offered.Profile))
        {
            var sdes = SdesCryptoSelector.SelectAnswer(offered.Crypto);
            if (sdes is not null)
                videoCrypto = [sdes.LocalAnswer];
        }

        // DTLS-keyed video needs a fingerprinted answer (RFC 5763), same identity as audio.
        SdpFingerprint? fingerprint = null;
        string? dtlsSetup = null;
        if (videoCrypto.Count == 0 && (remoteFp is not null || SdpSecurityInspector.IsDtlsProfile(offered.Profile)))
        {
            if (localOptions.Dtls is null || remoteFp is null)
                return null;

            fingerprint = new SdpFingerprint
            {
                Algorithm = localOptions.Dtls.Algorithm,
                Value = localOptions.Dtls.Fingerprint
            };
            dtlsSetup = ResolveAnswerSetup(offered.DtlsSetup ?? remoteOffer.DtlsSetup);
        }

        // Fail closed: a secure video m-line we could key neither via SDES nor DTLS — a keyless
        // SAVP profile, or an a=crypto whose suite we do not support — is declined, not answered
        // in the clear.
        if (videoCrypto.Count == 0 && fingerprint is null
            && (IsSdesSecuredProfile(offered.Profile) || offered.Crypto.Count > 0))
        {
            return null;
        }

        // Name+clock match only — NEVER the static-PT fallback of the audio path:
        // video PTs are dynamic, a bare PT match would answer a codec the peer never
        // offered. Payload types mirror the offer (RFC 3264 §6.1).
        var negotiated = SelectVideoCodecs(offered, video.Codecs);
        if (negotiated.Count == 0)
            return null;

        var acceptedPts = new HashSet<int>(negotiated.Select(c => c.PayloadType));

        // RTX (RFC 4588 §8.1): echo the repair codecs the peer offered for codecs we
        // accepted, so both sides agree on the rtx payload numbering.
        var (rtxCodecs, rtxFmtp) = VideoCodecCatalog.NegotiateRtx(offered, acceptedPts);
        var carriedFmtp = offered.Fmtp.Where(f => acceptedPts.Contains(f.PayloadType));

        return new SdpMediaDescription
        {
            MediaType = "video",
            Port = video.Port,
            Profile = ResolveAnswerProfile(offered.Profile),
            Codecs = [.. negotiated, .. rtxCodecs],
            Direction = ResolveAnswerDirection(offered.Direction, answerDirection),
            Fmtp = [.. carriedFmtp, .. rtxFmtp],
            RtcpFeedback = VideoCodecCatalog.NegotiateFeedback(offered.RtcpFeedback),
            Mid = offered.Mid,
            Msid = localOptions.VideoMsid,
            RtcpMux = offered.RtcpMux,
            Crypto = videoCrypto,
            Fingerprint = fingerprint,
            DtlsSetup = dtlsSetup,
            // ICE (RFC 8839): answer the video m-line with the session-shared ufrag/pwd plus our
            // own video host candidate so the peer can check the video 5-tuple, mirroring audio.
            IceUfrag = localOptions.Ice?.Ufrag,
            IcePwd = localOptions.Ice?.Pwd,
            IceOptions = localOptions.Ice?.Options,
            Candidates = video.Candidates,
            // RTP header extensions (RFC 8285 §5): echo the offered id for each URI we support,
            // dropping the rest — the answer confirms the negotiated id↔uri mapping.
            Extensions = BuildAnswerExtmaps(offered.Extensions, WithMidExtension(video.HeaderExtensionUris))
        };
    }

    // RFC 8285 §4.2: the one-byte header form uses ids 1..14 (0 is padding, 15 is reserved).
    private const int OneByteMaxExtensionId = 14;

    // Offer: assign sequential one-byte ids to the supported extension URIs (RFC 8285 §5). Only the
    // first 14 fit the one-byte form; any beyond that are dropped (the SDK's supported set is small).
    private static IReadOnlyList<SdpExtmap> BuildOfferExtmaps(IReadOnlyList<string> uris)
    {
        if (uris.Count == 0)
            return [];

        var extmaps = new List<SdpExtmap>(Math.Min(uris.Count, OneByteMaxExtensionId));
        for (var i = 0; i < uris.Count && i < OneByteMaxExtensionId; i++)
            extmaps.Add(new SdpExtmap { Id = i + 1, Uri = uris[i] });
        return extmaps;
    }

    // Send-side simulcast (RFC 8853): one a=rid per layer with direction "send", restricted to the primary
    // (first, non-RTX) video codec's payload type, plus one a=simulcast:send listing the layer ids in
    // order. Empty when no simulcast layer is configured (a single-stream video m-line).
    private static (IReadOnlyList<SdpRid> Rids, SdpSimulcast? Simulcast) BuildSimulcast(
        IReadOnlyList<string> sendRids, IReadOnlyList<SdpCodecDefinition> videoCodecs)
    {
        if (sendRids.Count == 0)
            return ([], null);

        var primaryPt = videoCodecs[0].PayloadType;
        var rids = sendRids
            .Select(rid => new SdpRid { Id = rid, Direction = "send", Restrictions = $"pt={primaryPt}" })
            .ToArray();
        return (rids, new SdpSimulcast { Send = sendRids });
    }

    // The answer echoes the MID SDES extension (RFC 9143) whenever the offer advertised it: adding the
    // MID URI to the supported set makes BuildAnswerExtmaps mirror the offered id (RFC 8843 §9 — the
    // same id the offer used on every m-line). A no-op when the offer carried no MID extension (outside
    // BUNDLE), so non-bundle answers are unchanged.
    private static IReadOnlyList<string> WithMidExtension(IReadOnlyList<string> supportedUris) =>
        [RtpHeaderExtensionUris.Mid, .. supportedUris];

    // Answer: for each offered extmap whose URI we support, echo it with the offered id (RFC 8285
    // §5 — the offerer owns the id assignment); unsupported extensions are dropped. Only one-byte
    // ids are echoed, since that is the form the SDK reads/writes.
    private static IReadOnlyList<SdpExtmap> BuildAnswerExtmaps(
        IReadOnlyList<SdpExtmap> offered, IReadOnlyList<string> supportedUris)
    {
        if (offered.Count == 0 || supportedUris.Count == 0)
            return [];

        var extmaps = new List<SdpExtmap>();
        foreach (var extmap in offered)
        {
            if (extmap.Id is < 1 or > OneByteMaxExtensionId)
                continue;
            if (supportedUris.Contains(extmap.Uri, StringComparer.Ordinal))
                extmaps.Add(new SdpExtmap { Id = extmap.Id, Uri = extmap.Uri });
        }
        return extmaps;
    }

    /// <summary>
    /// Intersects offered video codecs with the local capability set by name and clock
    /// rate. H.264 additionally requires an explicit <c>packetization-mode=1</c> fmtp —
    /// the packetisation layer always fragments large NALs as FU-A, which a mode-0-only
    /// peer (packetization-mode absent or 0, RFC 6184 §8.1) cannot receive.
    /// </summary>
    private static IReadOnlyList<SdpCodecDefinition> SelectVideoCodecs(
        SdpMediaDescription offered,
        IReadOnlyList<SdpCodecDefinition> localCodecs)
    {
        return offered.Codecs.Where(IsAcceptable).ToArray();

        bool IsAcceptable(SdpCodecDefinition candidate)
        {
            if (!VideoCodecCatalog.IsSupported(candidate.Name))
                return false;
            if (!localCodecs.Any(local =>
                    local.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)
                    && local.ClockRate == candidate.ClockRate))
            {
                return false;
            }

            return !candidate.Name.Equals("H264", StringComparison.OrdinalIgnoreCase)
                   || VideoCodecCatalog.HasPacketizationMode1(offered.Fmtp, candidate.PayloadType);
        }
    }

    /// <summary>
    /// Declines one offered m-line with the RFC 3264 §6 zero-port mirror (media type,
    /// profile, and formats preserved so the answer stays structurally valid).
    /// </summary>
    private static SdpMediaDescription BuildDisabledMirror(SdpMediaDescription offered) => new()
    {
        MediaType = offered.MediaType,
        Port = 0,
        Profile = offered.Profile,
        Codecs = offered.Codecs,
        Mid = offered.Mid,
        Direction = SdpMediaDirection.Inactive
    };

    // -------------------------------------------------------------------------
    // Disabled answer (zero-port mirror)
    // -------------------------------------------------------------------------

    private static SdpSessionDescription BuildDisabledAnswer(
        SdpSessionDescription remoteOffer,
        IPEndPoint localEndPoint,
        SdpMediaOptions? options)
    {
        var host = LocalEndPointHostResolver.ResolveHost(localEndPoint);
        var disabledMedia = remoteOffer.Media.Select(m => new SdpMediaDescription
        {
            MediaType = m.MediaType,
            Port = 0,
            Profile = m.Profile,
            Codecs = m.Codecs,
            Direction = SdpMediaDirection.Inactive
        }).ToArray();

        return new SdpSessionDescription
        {
            OriginAddress = host,
            ConnectionAddress = host,
            SessionDirection = SdpMediaDirection.Inactive,
            Media = disabledMedia,
            SessionId = options?.SessionId ?? 0,
            SessionVersion = options?.SessionVersion ?? 0
        };
    }

    // -------------------------------------------------------------------------
    // Direction resolution (RFC 3264 §6.1)
    // -------------------------------------------------------------------------

    private static SdpMediaDirection ResolveAnswerDirection(
        SdpMediaDirection offered,
        SdpMediaDirection local)
    {
        if (offered == SdpMediaDirection.Inactive || local == SdpMediaDirection.Inactive)
            return SdpMediaDirection.Inactive;

        if (offered == SdpMediaDirection.SendOnly)
        {
            return local switch
            {
                SdpMediaDirection.SendOnly => SdpMediaDirection.Inactive,
                _ => SdpMediaDirection.RecvOnly
            };
        }

        if (offered == SdpMediaDirection.RecvOnly)
        {
            return local switch
            {
                SdpMediaDirection.RecvOnly => SdpMediaDirection.Inactive,
                _ => SdpMediaDirection.SendOnly
            };
        }

        if (local == SdpMediaDirection.SendOnly)
            return SdpMediaDirection.SendOnly;
        if (local == SdpMediaDirection.RecvOnly)
            return SdpMediaDirection.RecvOnly;
        return SdpMediaDirection.SendRecv;
    }

    // -------------------------------------------------------------------------
    // DTLS setup role resolution (RFC 4145 §4)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the local DTLS setup role based on the remote's offered role.
    /// <list type="bullet">
    ///   <item><description><c>actpass</c> → local answers <c>active</c></description></item>
    ///   <item><description><c>active</c>  → local answers <c>passive</c></description></item>
    ///   <item><description><c>passive</c> → local answers <c>active</c></description></item>
    ///   <item><description><c>holdconn</c> or null → local answers <c>passive</c></description></item>
    /// </list>
    /// An answer MUST be <c>active</c> or <c>passive</c>, never <c>actpass</c> (RFC 5763 §5).
    /// <c>holdconn</c> (RFC 4145 §4 — establish no connection for now) has no valid answer role
    /// that keeps the connection held; we fall through to <c>passive</c> (server side) so the
    /// handshake can complete once the peer moves off hold, rather than emit an illegal role.
    /// </summary>
    private static string ResolveAnswerSetup(string? remoteSetup) =>
        remoteSetup?.ToLowerInvariant() switch
        {
            "actpass" => "active",
            "active" => "passive",
            "passive" => "active",
            // RFC 5763 §5: an answer MUST be active or passive — never actpass. With no
            // remote a=setup the offer defaults to active (RFC 4145 §4), so we take the
            // passive (server) side.
            _ => "passive"
        };

    // -------------------------------------------------------------------------
    // Profile resolution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Mirrors the offered profile in the answer.
    /// DTLS and SAVP profiles are passed through; plain RTP stays plain RTP.
    /// </summary>
    private static string ResolveAnswerProfile(string offeredProfile) =>
        offeredProfile.ToUpperInvariant() switch
        {
            "UDP/TLS/RTP/SAVPF" => "UDP/TLS/RTP/SAVPF",
            "UDP/TLS/RTP/SAVP" => "UDP/TLS/RTP/SAVP",
            "RTP/SAVPF" => "RTP/SAVPF",
            "RTP/SAVP" => "RTP/SAVP",
            _ => offeredProfile
        };

    /// <summary>
    /// Returns true for profiles that are keyed via SDES <c>a=crypto</c> (RFC 4568) —
    /// i.e. secure RTP without a DTLS transport. These cannot be answered keyless.
    /// </summary>
    private static bool IsSdesSecuredProfile(string offeredProfile) =>
        offeredProfile.Equals("RTP/SAVP", StringComparison.OrdinalIgnoreCase)
        || offeredProfile.Equals("RTP/SAVPF", StringComparison.OrdinalIgnoreCase);
}
