using System.Net;
using System.Collections.ObjectModel;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Sdes;

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
            DefaultCodecs,
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
                DefaultCodecs,
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
    /// <param name="remoteSdp">The far end's SDP (their offer or answer). Source of the remote key.</param>
    /// <param name="localEndPoint">Local UDP endpoint to bind RTP to.</param>
    /// <param name="localSdp">
    /// Optional: the SDP we sent (our answer or offer). When it carries a matching SDES
    /// <c>a=crypto</c> line, both keys are composed into
    /// <see cref="CallMediaParameters.SrtpKeys"/>. When null, no SDES keys are carried.
    /// </param>
    public static CallMediaParameters? TryParseMediaParameters(
        string remoteSdp,
        IPEndPoint localEndPoint,
        string? localSdp = null)
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

            var telephoneEventPayloadType = ResolveTelephoneEventPayloadType(audio);
            var primaryCodec = SelectPrimaryCodec(audio.Codecs, telephoneEventPayloadType);
            if (primaryCodec is null) return null;

            var clockRate   = primaryCodec.ClockRate > 0 ? primaryCodec.ClockRate : 8000;
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

            // SDES key material (RFC 4568): remote key from the peer's a=crypto, local key from
            // the a=crypto we advertised in localSdp. Null unless both sides carry a supported suite.
            var remoteCrypto = audio.Crypto.Count > 0 ? audio.Crypto[0] : null;
            var localCrypto = TryGetFirstAudioCrypto(localSdp);
            var srtpKeys = SdesKeyExchange.TryBuildSessionKeyMaterial(localCrypto, remoteCrypto);

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
                RemoteIceEndOfCandidates = audio.EndOfCandidates,
                SrtpKeys = srtpKeys
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an SDP body and returns the first <c>a=crypto</c> line of its first audio m-line.
    /// Returns <see langword="null"/> for null/blank input, a missing audio section, an absent
    /// crypto line, or any parse failure — SDES key extraction is best-effort and never throws.
    /// </summary>
    private static SdpCryptoAttribute? TryGetFirstAudioCrypto(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
            return null;

        try
        {
            var parsed = Parser.Parse(sdp);
            var audio = parsed.Media.FirstOrDefault(
                m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));
            return audio is { Crypto.Count: > 0 } ? audio.Crypto[0] : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Malformed local SDP: degrade to "no local key" so valid remote media still parses.
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

    private static int? ResolveTelephoneEventPayloadType(SdpMediaDescription audio)
    {
        foreach (var codec in audio.Codecs)
        {
            if (codec.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase)
                && IsValidPayloadType(codec.PayloadType))
            {
                return codec.PayloadType;
            }
        }

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
        int? telephoneEventPayloadType)
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

            var rank = GetCodecPreferenceRank(codec, i);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = codec;
            }
        }

        return best;
    }

    private static int GetCodecPreferenceRank(SdpCodecDefinition codec, int position)
    {
        var normalized = NormalizeCodecName(codec);
        return normalized switch
        {
            "G722" => 0,
            "PCMA" => 10 + position,
            "PCMU" => 20 + position,
            _ => 100 + position
        };
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
            return new SdpMediaOptions { OfferSdes = options.OfferSdes };

        return new SdpMediaOptions
        {
            OfferSdes = options.OfferSdes,
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
