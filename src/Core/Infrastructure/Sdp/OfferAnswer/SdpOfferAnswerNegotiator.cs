using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
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
        var fmtp = BuildFmtpForCodecs(codecs);
        var dtls = options?.Dtls;
        var ice = options?.Ice;
        var crypto = options?.Crypto ?? [];

        // Profile selection: DTLS wins (RFC 5763, UDP/TLS/RTP/SAVPF); otherwise SDES
        // a=crypto lines key an RTP/SAVP profile (RFC 4568); otherwise plain RTP/AVP.
        var profile = dtls is not null
            ? "UDP/TLS/RTP/SAVPF"
            : crypto.Count > 0
                ? "RTP/SAVP"
                : "RTP/AVP";

        // Video (WebRTC phase 2): a second m-line when requested. SDES keying is per-m-line
        // (RFC 4568): the video m-line carries its own a=crypto (options.Video.Crypto), keyed
        // independently of audio, on the same secure profile.
        var offerVideo = options?.Video is not null;

        // BUNDLE: session-level group + media-level mid
        string? group = null;
        string? mid = null;
        if (options?.Bundle == true)
        {
            group = offerVideo ? "BUNDLE audio video" : "BUNDLE audio";
            mid = "audio";
        }

        var media = new SdpMediaDescription
        {
            MediaType = "audio",
            Port = localEndPoint.Port,
            Profile = profile,
            Direction = direction,
            Codecs = codecs,
            Fmtp = fmtp,
            Mid = mid,
            Crypto = crypto,
            RtcpMux = options?.RtcpMux == true,
            IceUfrag = ice?.Ufrag,
            IcePwd = ice?.Pwd,
            IceOptions = ice?.Options,
            Candidates = ice?.Candidates ?? [],
            Fingerprint = dtls is not null
                ? new SdpFingerprint { Algorithm = dtls.Algorithm, Value = dtls.Fingerprint }
                : null,
            DtlsSetup = dtls?.Setup
        };

        var mediaLines = new List<SdpMediaDescription> { media };
        if (offerVideo)
        {
            var video = options!.Video!;
            // RTX repair streams (RFC 4588 §8.1): one rtx payload type per video codec,
            // appended to the m-line with an apt fmtp binding it to the original.
            var (rtxCodecs, rtxFmtp) = VideoCodecCatalog.BuildRtx(video.Codecs);
            mediaLines.Add(new SdpMediaDescription
            {
                MediaType = "video",
                Port = video.Port,
                Profile = profile,
                Direction = direction,
                Codecs = [.. video.Codecs, .. rtxCodecs],
                Fmtp = [.. VideoCodecCatalog.BuildFmtp(video.Codecs), .. rtxFmtp],
                RtcpFeedback = VideoCodecCatalog.StandardFeedback,
                Mid = options.Bundle == true ? "video" : null,
                RtcpMux = options.RtcpMux == true,
                Crypto = video.Crypto,
                // ICE (RFC 8839): advertise the session-shared ufrag/pwd on the video m-line so a
                // peer applies ICE to the video 5-tuple (this SDK shares one credential set across
                // m-lines — no BUNDLE). Per-m-line video candidates are a documented follow-up;
                // the consent path derives the video 5-tuple from the m-line address/port.
                IceUfrag = ice?.Ufrag,
                IcePwd = ice?.Pwd,
                IceOptions = ice?.Options,
                Candidates = video.Candidates,
                Fingerprint = dtls is not null
                    ? new SdpFingerprint { Algorithm = dtls.Algorithm, Value = dtls.Fingerprint }
                    : null,
                DtlsSetup = dtls?.Setup
            });
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

        var negotiated = NegotiateCodecs(offeredAudio.Codecs, localCapabilities);

        // At least one real audio codec must be negotiated — an answer carrying only
        // telephone-event would be an audio-less call. Reachable when the caller pins
        // an opt-in codec (e.g. Opus) that the peer does not offer: negotiation fails
        // (488) instead of producing a broken answer.
        if (!negotiated.Any(c => !c.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase)))
            return new SdpOfferAnswerResult { Success = false };

        var host = LocalEndPointHostResolver.ResolveHost(localEndPoint);
        var answerDirection = ResolveAnswerDirection(offeredAudio.Direction, localDirection);

        // Carry fmtp from remote offer for accepted payload types (RFC 3264 §6.1).
        var acceptedPts = new HashSet<int>(negotiated.Select(c => c.PayloadType));
        var carriedFmtp = offeredAudio.Fmtp
            .Where(f => acceptedPts.Contains(f.PayloadType))
            .ToArray();

        // Reflect ptime when the remote offer specifies one (RFC 3264 §6.1).
        var ptime = offeredAudio.Ptime;

        // --- rtcp-mux (RFC 5761): mirror when offered ---
        var rtcpMux = offeredAudio.RtcpMux || localOptions?.RtcpMux == true;

        // --- BUNDLE/MID (RFC 5888): mirror mid from remote ---
        var mid = offeredAudio.Mid;
        string? group = null;
        if (mid is not null && remoteOffer.Group is not null
            && remoteOffer.Group.StartsWith("BUNDLE", StringComparison.OrdinalIgnoreCase))
        {
            group = remoteOffer.Group;
        }

        // --- SDES crypto (RFC 4568): answer the first supported suite with our OWN key ---
        // (§5.1.3 — echoing the offerer's key would put the same keystream on both
        // directions; the peer would also fail to decrypt our stream.)
        IReadOnlyList<SdpCryptoAttribute> crypto = [];
        SdpCryptoAttribute? localCrypto = null;
        SdpCryptoAttribute? remoteCrypto = null;
        if (offeredAudio.Crypto.Count > 0)
        {
            var sdes = SdesCryptoSelector.SelectAnswer(offeredAudio.Crypto);
            if (sdes is not null)
            {
                localCrypto = sdes.LocalAnswer;
                remoteCrypto = sdes.RemoteOffer;
                crypto = [localCrypto];
            }
        }

        // RFC 3264 §5.1: the answer keeps the offered profile. An SDES-secured profile
        // without a negotiated key cannot be answered (keyless SAVP) and silently
        // downgrading to plain RTP is not allowed — reject instead. DTLS-keyed profiles
        // (UDP/TLS/…) are unaffected; a plain AVP offer carrying an unsupported a=crypto
        // falls back to an unencrypted answer, which stays legal for AVP.
        // --- DTLS (RFC 5763): resolve fingerprint and setup role ---
        SdpFingerprint? fingerprint = null;
        string? dtlsSetup = null;
        var remoteFp = offeredAudio.Fingerprint ?? remoteOffer.Fingerprint;
        var remoteSetup = offeredAudio.DtlsSetup ?? remoteOffer.DtlsSetup;

        // Answer with DTLS only when the peer actually offered it (remote fingerprint
        // present) and SDES did not already win — the keying methods are mutually
        // exclusive per m-line (RFC 5763). Note RFC 5763 §6.6 signals DTLS on RTP/SAVP(F)
        // profiles too, not only UDP/TLS/* — the fingerprint decides, not the profile.
        if (localOptions?.Dtls is not null && remoteFp is not null && localCrypto is null)
        {
            fingerprint = new SdpFingerprint
            {
                Algorithm = localOptions.Dtls.Algorithm,
                Value = localOptions.Dtls.Fingerprint
            };
            dtlsSetup = ResolveAnswerSetup(remoteSetup);
        }

        // Fail closed: a secure-profile offer we can key neither via SDES nor via DTLS
        // cannot be answered — silently downgrading to plain RTP is not allowed.
        if (localCrypto is null && fingerprint is null && IsSdesSecuredProfile(offeredAudio.Profile))
            return new SdpOfferAnswerResult { Success = false };

        // A DTLS-keyed profile additionally requires a DTLS answer — an SDES answer on
        // UDP/TLS/* would contradict the profile's keying method.
        if (fingerprint is null && IsDtlsSecuredProfile(offeredAudio.Profile))
            return new SdpOfferAnswerResult { Success = false };

        // --- ICE credentials (RFC 8839) ---
        var ice = localOptions?.Ice;

        // --- Profile: mirror remote profile for DTLS/SAVP ---
        var profile = ResolveAnswerProfile(offeredAudio.Profile);

        var answerMedia = new SdpMediaDescription
        {
            MediaType = "audio",
            Port = localEndPoint.Port,
            Profile = profile,
            Codecs = negotiated,
            Direction = answerDirection,
            Fmtp = carriedFmtp,
            Ptime = ptime,
            Mid = mid,
            RtcpMux = rtcpMux,
            Crypto = crypto,
            Fingerprint = fingerprint,
            DtlsSetup = dtlsSetup,
            IceUfrag = ice?.Ufrag,
            IcePwd = ice?.Pwd,
            IceOptions = ice?.Options,
            Candidates = ice?.Candidates ?? []
        };

        // RFC 3264 §6: the answer must carry one m-line per offered m-line, in offer
        // order. Video is negotiated when enabled; anything else (or unanswerable
        // video) is declined with a zero-port mirror.
        var answerLines = new List<SdpMediaDescription>(remoteOffer.Media.Count);
        var videoAnswered = false;
        foreach (var offered in remoteOffer.Media)
        {
            if (ReferenceEquals(offered, offeredAudio))
            {
                answerLines.Add(answerMedia);
                continue;
            }

            // Only the first video m-line is negotiated — a second one (screenshare
            // pattern) would share the single local video port and break demux.
            var videoAnswer = videoAnswered
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
            NegotiatedCodecs = negotiated,
            RtcpMuxNegotiated = rtcpMux,
            RemoteFingerprint = remoteFp,
            RemoteDtlsSetup = remoteSetup,
            NegotiatedCrypto = remoteCrypto,
            LocalCrypto = localCrypto
        };
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

        // Fallback: static payload type intersection for codecs without rtpmap.
        if (negotiated.Count == 0)
        {
            var localByPt = localCapabilities.ToDictionary(c => c.PayloadType);
            foreach (var offer in offered)
            {
                if (localByPt.TryGetValue(offer.PayloadType, out var local))
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
        if (offered.Crypto.Count > 0 && !IsDtlsSecuredProfile(offered.Profile))
        {
            var sdes = SdesCryptoSelector.SelectAnswer(offered.Crypto);
            if (sdes is not null)
                videoCrypto = [sdes.LocalAnswer];
        }

        // DTLS-keyed video needs a fingerprinted answer (RFC 5763), same identity as audio.
        SdpFingerprint? fingerprint = null;
        string? dtlsSetup = null;
        if (videoCrypto.Count == 0 && (remoteFp is not null || IsDtlsSecuredProfile(offered.Profile)))
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
            RtcpMux = offered.RtcpMux,
            Crypto = videoCrypto,
            Fingerprint = fingerprint,
            DtlsSetup = dtlsSetup,
            // ICE (RFC 8839): answer the video m-line with the session-shared ufrag/pwd plus our
            // own video host candidate so the peer can check the video 5-tuple, mirroring audio.
            IceUfrag = localOptions.Ice?.Ufrag,
            IcePwd = localOptions.Ice?.Pwd,
            IceOptions = localOptions.Ice?.Options,
            Candidates = video.Candidates
        };
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
    ///   <item><description><c>holdconn</c> or null → local answers <c>actpass</c></description></item>
    /// </list>
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

    private static bool IsDtlsSecuredProfile(string offeredProfile) =>
        offeredProfile.StartsWith("UDP/TLS/", StringComparison.OrdinalIgnoreCase);
}
