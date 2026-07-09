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

        // BUNDLE: session-level group + media-level mid
        string? group = null;
        string? mid = null;
        if (options?.Bundle == true)
        {
            group = "BUNDLE audio";
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

        return new SdpSessionDescription
        {
            OriginAddress = host,
            ConnectionAddress = host,
            SessionDirection = direction,
            Group = group,
            Media = [media],
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
        if (localCrypto is null && IsSdesSecuredProfile(offeredAudio.Profile))
            return new SdpOfferAnswerResult { Success = false };

        // --- DTLS (RFC 5763): resolve fingerprint and setup role ---
        SdpFingerprint? fingerprint = null;
        string? dtlsSetup = null;
        var remoteFp = offeredAudio.Fingerprint ?? remoteOffer.Fingerprint;
        var remoteSetup = offeredAudio.DtlsSetup ?? remoteOffer.DtlsSetup;

        if (localOptions?.Dtls is not null)
        {
            fingerprint = new SdpFingerprint
            {
                Algorithm = localOptions.Dtls.Algorithm,
                Value = localOptions.Dtls.Fingerprint
            };
            dtlsSetup = ResolveAnswerSetup(remoteSetup);
        }
        else if (remoteFp is not null && localOptions?.Dtls is null)
        {
            // Remote wants DTLS but we have no local fingerprint — cannot answer DTLS.
            // Proceed without DTLS attributes; caller may treat this as a warning.
        }

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

        var answer = new SdpSessionDescription
        {
            OriginAddress = host,
            ConnectionAddress = host,
            SessionDirection = answerDirection,
            Group = group,
            Media = [answerMedia],
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
            _ => "actpass"
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
