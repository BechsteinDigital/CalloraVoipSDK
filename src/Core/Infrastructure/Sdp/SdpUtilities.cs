using System.Net;
using System.Collections.ObjectModel;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp;

/// <summary>
/// Shared SDP helpers for creating minimal offers/answers and interpreting hold intent.
/// These helpers are protocol-agnostic and can be reused across SIP, RTP, and SRTP modules.
/// </summary>
internal static class SdpUtilities
{
    private static readonly ISdpOfferAnswerNegotiator Negotiator = new SdpOfferAnswerNegotiator();
    private static readonly ISdpSessionParser Parser = new SdpSessionParser();
    private static readonly ISdpSessionSerializer Serializer = new SdpSessionSerializer();
    private static readonly IReadOnlyList<SdpCodecDefinition> DefaultCodecs =
    [
        new SdpCodecDefinition { PayloadType = 9,   Name = "G722",            ClockRate = 8000 },
        new SdpCodecDefinition { PayloadType = 8,   Name = "PCMA",            ClockRate = 8000 },
        new SdpCodecDefinition { PayloadType = 0,   Name = "PCMU",            ClockRate = 8000 },
        // RFC 4733: DTMF telephone-event (dynamic PT 101, fmtp 0-16 added automatically by negotiator)
        new SdpCodecDefinition { PayloadType = 101, Name = "telephone-event", ClockRate = 8000 }
    ];

    // Codecs the SDK implements but does not offer by default — selectable via
    // SdkConfiguration.PreferredAudioCodecs. Opus per RFC 7587: the encoding is always
    // announced as opus/48000/2 (dynamic PT), regardless of the operating mode.
    private static readonly IReadOnlyList<SdpCodecDefinition> OptInCodecs =
    [
        new SdpCodecDefinition { PayloadType = 107, Name = "opus", ClockRate = 48000, Channels = 2 }
    ];

    /// <summary>
    /// Extracts the first SDES crypto attribute with a supported suite and an inline key
    /// from the first active audio m-line of an SDP body (RFC 4568). Used to recover key
    /// material at the media layer from the SDP that actually went over the wire — both
    /// the peer's offer (inbound decrypt key) and our serialized answer (outbound encrypt
    /// key). Returns <see langword="null"/> on plain-RTP SDP or parse failure.
    /// </summary>
    public static SdpCryptoAttribute? TryExtractAudioCrypto(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
            return null;

        try
        {
            var parsed = Parser.Parse(sdp);
            var audio = parsed.Media.FirstOrDefault(
                m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase)
                     && !m.Disabled
                     && m.Port > 0);
            if (audio is null)
                return null;

            return audio.Crypto.FirstOrDefault(
                c => SdesCryptoSelector.TryMapSuite(c.CryptoSuite) is not null
                     && c.KeyParams.StartsWith("inline:", StringComparison.OrdinalIgnoreCase));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a default SDP offer for local capabilities.
    /// </summary>
    public static string BuildDefaultSdp(
        IPEndPoint localEndPoint,
        bool hold,
        SdpMediaNegotiationOptions? options = null)
    {
        var direction = hold ? SdpMediaDirection.SendOnly : SdpMediaDirection.SendRecv;
        var offer = Negotiator.CreateOffer(
            localEndPoint,
            ResolveLocalCodecs(options),
            direction,
            ConvertOptions(options));
        return Serializer.Serialize(offer);
    }

    /// <summary>
    /// Negotiates an SDP answer against remote offer text and local capabilities.
    /// </summary>
    public static string? TryBuildNegotiatedAnswer(
        string remoteOffer,
        IPEndPoint localEndPoint,
        bool hold,
        SdpMediaNegotiationOptions? localOptions = null)
    {
        try
        {
            var parsedOffer = Parser.Parse(remoteOffer);
            var localDirection = hold ? SdpMediaDirection.SendOnly : SdpMediaDirection.SendRecv;
            var result = Negotiator.NegotiateAnswer(
                parsedOffer,
                localEndPoint,
                ResolveLocalCodecs(localOptions),
                localDirection,
                ConvertOptions(localOptions));
            return result.Success && result.Answer is not null
                ? Serializer.Serialize(result.Answer)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a remote SDP body and extracts the negotiated media parameters needed to
    /// create an RTP session. Returns <see langword="null"/> when the SDP cannot be parsed
    /// or contains no usable audio stream.
    /// </summary>
    public static CallMediaParameters? TryParseMediaParameters(
        string remoteSdp,
        IPEndPoint localEndPoint,
        SdpMediaNegotiationOptions? localOptions = null)
    {
        if (string.IsNullOrWhiteSpace(remoteSdp)) return null;
        try
        {
            var parsed = Parser.Parse(remoteSdp);
            var audio = parsed.Media.FirstOrDefault(
                m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));
            if (audio is null || audio.Disabled || audio.Port <= 0) return null;

            var connectionAddress = audio.ConnectionAddress
                ?? parsed.ConnectionAddress
                ?? string.Empty;
            if (!IPAddress.TryParse(connectionAddress, out var remoteIp)) return null;

            var anyTelephoneEventPayloadType = ResolveTelephoneEventPayloadType(audio, preferredClockRate: null);
            var primaryCodec = SelectPrimaryCodec(
                audio.Codecs,
                anyTelephoneEventPayloadType,
                localOptions?.PreferredCodecNames);
            if (primaryCodec is null) return null;

            var clockRate   = primaryCodec.ClockRate > 0 ? primaryCodec.ClockRate : 8000;

            // RFC 4733: prefer the telephone-event line whose clock matches the negotiated
            // audio codec (offers like sipgate's list one per clock, e.g. 101/48000 for
            // Opus and 113/8000 for G.711); fall back to any offered event line.
            var telephoneEventPayloadType = ResolveTelephoneEventPayloadType(audio, clockRate);
            var ptimeMs     = (audio.Ptime ?? 0) > 0 ? audio.Ptime!.Value : 20;
            var samplesPerPacket = clockRate * ptimeMs / 1000;
            var codecMap = BuildCodecMap(audio.Codecs, telephoneEventPayloadType);
            var codecName = NormalizeCodecName(primaryCodec);
            var isSrtpSignaled = SdpSecurityInspector.IsSrtpSignaled(parsed, audio);
            var rtcpMux = audio.RtcpMux;
            var remoteRtcpPort = ResolveRtcpPort(
                audio.Port,
                audio.RtcpPort,
                rtcpMux);
            var localRtcpPort = ResolveRtcpPort(
                localEndPoint.Port,
                rtcpPortFromSdp: null,
                rtcpMux);
            var remoteEndPoint = new IPEndPoint(remoteIp, audio.Port);
            var localRtcpEndPoint = new IPEndPoint(localEndPoint.Address, localRtcpPort);
            var remoteRtcpEndPoint = new IPEndPoint(remoteIp, remoteRtcpPort);
            var remoteIceUfrag = audio.IceUfrag ?? parsed.IceUfrag;
            var remoteIcePwd = audio.IcePwd ?? parsed.IcePwd;
            var remoteIceOptions = audio.IceOptions ?? parsed.IceOptions;
            var remoteIceCandidates = BuildCallIceCandidates(audio.Candidates);
            var iceEnabled = !string.IsNullOrWhiteSpace(remoteIceUfrag)
                             && !string.IsNullOrWhiteSpace(remoteIcePwd)
                             && remoteIceCandidates.Count > 0;

            return new CallMediaParameters
            {
                LocalEndPoint    = localEndPoint,
                RemoteEndPoint   = remoteEndPoint,
                RtcpMux          = rtcpMux,
                LocalRtcpEndPoint = localRtcpEndPoint,
                RemoteRtcpEndPoint = remoteRtcpEndPoint,
                PayloadType      = primaryCodec.PayloadType,
                CodecName        = codecName,
                PayloadTypeCodecMap = codecMap,
                TelephoneEventPayloadType = telephoneEventPayloadType,
                ClockRate        = clockRate,
                SamplesPerPacket = samplesPerPacket,
                MediaProfile     = audio.Profile,
                IsSrtpNegotiated = isSrtpSignaled,
                IceEnabled = iceEnabled,
                RemoteIceUfrag = remoteIceUfrag,
                RemoteIcePwd = remoteIcePwd,
                RemoteIceOptions = remoteIceOptions,
                RemoteIceCandidates = remoteIceCandidates,
                RemoteIceEndOfCandidates = audio.EndOfCandidates
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true when SDP indicates remote hold semantics.
    /// </summary>
    public static bool IsRemoteHoldSdp(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return false;

        try
        {
            var parsed = Parser.Parse(sdp);
            var audio = parsed.Media.FirstOrDefault(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));
            var direction = audio?.Direction ?? parsed.SessionDirection;
            return direction is SdpMediaDirection.SendOnly or SdpMediaDirection.Inactive;
        }
        catch
        {
            return sdp.Contains("a=sendonly", StringComparison.OrdinalIgnoreCase)
                   || sdp.Contains("a=inactive", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyDictionary<int, string> BuildCodecMap(
        IReadOnlyList<SdpCodecDefinition> codecs,
        int? telephoneEventPayloadType)
    {
        if (codecs.Count == 0 && telephoneEventPayloadType is null)
            return new ReadOnlyDictionary<int, string>(new Dictionary<int, string>(capacity: 0));

        var map = new Dictionary<int, string>(capacity: codecs.Count + (telephoneEventPayloadType is null ? 0 : 1));
        foreach (var codec in codecs)
            map[codec.PayloadType] = NormalizeCodecName(codec);

        if (telephoneEventPayloadType is >= 0 and <= 127)
            map[telephoneEventPayloadType.Value] = "TELEPHONE-EVENT";

        return new ReadOnlyDictionary<int, string>(map);
    }

    private static int? ResolveTelephoneEventPayloadType(SdpMediaDescription audio, int? preferredClockRate)
    {
        int? firstMatch = null;
        foreach (var codec in audio.Codecs)
        {
            if (!codec.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase)
                || !IsValidPayloadType(codec.PayloadType))
            {
                continue;
            }

            // RFC 4733 §2.1: the event stream shares the audio stream's timestamp clock —
            // when the offer lists one event line per clock, pick the matching one.
            if (preferredClockRate is { } clock && codec.ClockRate == clock)
                return codec.PayloadType;

            firstMatch ??= codec.PayloadType;
        }

        if (firstMatch is not null)
            return firstMatch;

        foreach (var fmtp in audio.Fmtp)
        {
            if (!IsValidPayloadType(fmtp.PayloadType))
                continue;

            if (LooksLikeTelephoneEventFmtp(fmtp.Parameters))
                return fmtp.PayloadType;
        }

        return null;
    }

    private static string NormalizeCodecName(SdpCodecDefinition codec)
    {
        if (!string.IsNullOrWhiteSpace(codec.Name)
            && !codec.Name.StartsWith("PT", StringComparison.OrdinalIgnoreCase))
        {
            return codec.Name.Trim().ToUpperInvariant();
        }

        return codec.PayloadType switch
        {
            0 => "PCMU",
            8 => "PCMA",
            9 => "G722",
            _ => $"PT{codec.PayloadType}"
        };
    }

    private static SdpCodecDefinition? SelectPrimaryCodec(
        IReadOnlyList<SdpCodecDefinition> codecs,
        int? telephoneEventPayloadType,
        IReadOnlyList<string>? preferredCodecNames = null)
    {
        SdpCodecDefinition? best = null;
        var bestRank = int.MaxValue;

        for (var i = 0; i < codecs.Count; i++)
        {
            var codec = codecs[i];
            if (codec.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase))
                continue;

            if (telephoneEventPayloadType == codec.PayloadType)
                continue;

            var rank = GetCodecPreferenceRank(codec, i, preferredCodecNames);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = codec;
            }
        }

        return best;
    }

    private static int GetCodecPreferenceRank(
        SdpCodecDefinition codec,
        int position,
        IReadOnlyList<string>? preferredCodecNames = null)
    {
        var normalized = NormalizeCodecName(codec);

        // An explicit local preference list overrides the built-in ranking: rank equals
        // the index in the list; codecs not on the list lose against every listed one.
        if (preferredCodecNames is { Count: > 0 })
        {
            for (var i = 0; i < preferredCodecNames.Count; i++)
            {
                if (normalized.Equals(preferredCodecNames[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 1000 + position;
        }

        return normalized switch
        {
            "G722" => 0,
            "PCMA" => 10 + position,
            "PCMU" => 20 + position,
            _ => 100 + position
        };
    }

    /// <summary>
    /// Resolves the local codec capability list for offers/answers. An explicit preference
    /// filters and reorders the SDK defaults (telephone-event is always kept); when no
    /// preferred name matches a supported codec, the defaults are used unchanged.
    /// </summary>
    private static IReadOnlyList<SdpCodecDefinition> ResolveLocalCodecs(
        SdpMediaNegotiationOptions? options)
    {
        var preferred = options?.PreferredCodecNames;
        if (preferred is null || preferred.Count == 0)
            return DefaultCodecs;

        var resolved = new List<SdpCodecDefinition>(preferred.Count + 1);
        foreach (var name in preferred)
        {
            // Preferred names resolve against defaults plus opt-in codecs (e.g. Opus),
            // so a codec can be enabled explicitly without changing the default offer.
            var match = DefaultCodecs.Concat(OptInCodecs).FirstOrDefault(c =>
                !c.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase)
                && NormalizeCodecName(c).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !resolved.Contains(match))
                resolved.Add(match);
        }

        if (resolved.Count == 0)
            return DefaultCodecs;

        resolved.Add(DefaultCodecs.First(c =>
            c.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase)));
        return resolved;
    }

    private static int ResolveRtcpPort(int rtpPort, int? rtcpPortFromSdp, bool rtcpMux)
    {
        if (rtcpMux)
            return rtpPort;

        if (rtcpPortFromSdp is > 0)
            return rtcpPortFromSdp.Value;

        return checked(rtpPort + 1);
    }

    private static bool IsValidPayloadType(int payloadType)
        => payloadType is >= 0 and <= 127;

    private static bool LooksLikeTelephoneEventFmtp(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return false;

        var normalized = parameters.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("0-16", StringComparison.Ordinal);
    }

    private static SdpMediaOptions? ConvertOptions(SdpMediaNegotiationOptions? options)
    {
        if (options is null)
            return null;

        var ice = options.Ice;
        if (ice is null)
            return new SdpMediaOptions();

        return new SdpMediaOptions
        {
            Ice = new SdpIceParameters
            {
                Ufrag = ice.Ufrag,
                Pwd = ice.Pwd,
                Options = ice.Options,
                Candidates = ice.Candidates.Select(c => new SdpIceCandidate
                {
                    Foundation = c.Foundation,
                    Component = c.Component,
                    Transport = c.Transport,
                    Priority = c.Priority,
                    Address = c.Address,
                    Port = c.Port,
                    Type = c.Type,
                    RelatedAddress = c.RelatedAddress,
                    RelatedPort = c.RelatedPort,
                    Generation = c.Generation,
                    Ufrag = c.Ufrag,
                    NetworkId = c.NetworkId
                }).ToArray()
            }
        };
    }

    private static IReadOnlyList<CallIceCandidate> BuildCallIceCandidates(IReadOnlyList<SdpIceCandidate> candidates)
    {
        if (candidates.Count == 0)
            return [];

        var mapped = new List<CallIceCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            mapped.Add(new CallIceCandidate
            {
                Foundation = candidate.Foundation,
                Component = candidate.Component,
                Transport = candidate.Transport,
                Priority = candidate.Priority,
                Address = candidate.Address,
                Port = candidate.Port,
                Type = candidate.Type,
                RelatedAddress = candidate.RelatedAddress,
                RelatedPort = candidate.RelatedPort,
                Generation = candidate.Generation,
                Ufrag = candidate.Ufrag,
                NetworkId = candidate.NetworkId
            });
        }

        return mapped;
    }
}
