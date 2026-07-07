using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Sdes;

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
    /// <summary>
    /// Default SDES crypto suite advertised in an SRTP offer (RFC 4568 §6.2). AES-128 CM with
    /// an 80-bit HMAC-SHA1 auth tag is the mandatory-to-implement, most widely interoperable suite.
    /// </summary>
    private const string DefaultOfferCryptoSuite = "AES_CM_128_HMAC_SHA1_80";

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

        // Transport profile + SDES keying (RFC 4568 / RFC 5763):
        //  - DTLS parameters present    → UDP/TLS/RTP/SAVPF (DTLS-SRTP, keys out of band).
        //  - OfferSdes requested        → RTP/SAVP with a freshly generated a=crypto line.
        //  - otherwise                  → plain RTP/AVP (unchanged default, no regression).
        string profile;
        IReadOnlyList<SdpCryptoAttribute> crypto = [];
        if (dtls is not null)
        {
            profile = "UDP/TLS/RTP/SAVPF";
        }
        else if (options?.OfferSdes == true
                 && SdesKeyExchange.TryCreateOfferCrypto(DefaultOfferCryptoSuite) is { } offerCrypto)
        {
            profile = "RTP/SAVP";
            crypto = [offerCrypto];
        }
        else
        {
            profile = "RTP/AVP";
        }

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
            Media = [media]
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
                Answer = BuildDisabledAnswer(remoteOffer, localEndPoint),
                NegotiatedCodecs = []
            };
        }

        var negotiated = NegotiateCodecs(offeredAudio.Codecs, localCapabilities);

        if (negotiated.Count == 0)
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

        // --- SDES crypto (RFC 4568 §6.1): accept the first offered suite we support and
        //     generate our OWN local key. We must never echo the offerer's key back. ---
        IReadOnlyList<SdpCryptoAttribute> crypto = [];
        SdpCryptoAttribute? acceptedRemoteCrypto = null;
        SdpCryptoAttribute? localCrypto = null;
        foreach (var offered in offeredAudio.Crypto)
        {
            var generatedLocal = SdesKeyExchange.TryCreateLocalCrypto(offered);
            if (generatedLocal is null)
                continue; // Unsupported suite — try the next offered crypto line.

            acceptedRemoteCrypto = offered;
            localCrypto = generatedLocal;
            crypto = [generatedLocal];
            break;
        }

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
            Media = [answerMedia]
        };

        return new SdpOfferAnswerResult
        {
            Success = true,
            Answer = answer,
            NegotiatedCodecs = negotiated,
            RtcpMuxNegotiated = rtcpMux,
            RemoteFingerprint = remoteFp,
            RemoteDtlsSetup = remoteSetup,
            NegotiatedCrypto = acceptedRemoteCrypto,
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
        return $"{codec.Name.ToUpperInvariant()}:{codec.ClockRate}{channels}";
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
        IPEndPoint localEndPoint)
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
            Media = disabledMedia
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
}
