using CalloraVoipSdk.Core.Application.Ports.Media;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Application.Media.Sessions;

/// <summary>
/// Transcodes audio between the negotiated wire codec and the fixed bridge (tap) codec so a
/// media consumer that only speaks one codec — e.g. the OpenAI realtime bridge, which is
/// G.711 µ-law only — keeps working when the SIP peer negotiates a different codec.
/// <para>
/// Only the µ-law tap is supported today. The intermediate is always PCM16 at the 8 kHz
/// telephony rate, so no resampler is needed: Opus decodes/encodes directly at 8 kHz and the
/// G.711 variants are already 8 kHz. Wire codecs that are not 8 kHz-native (e.g. G.722 at
/// 16 kHz) are not bridged here — that needs a resampler and is out of scope.
/// </para>
/// One instance serves one call leg and is not thread-safe by itself; the media session
/// invokes inbound (WireToTap) and outbound (TapToWire) from its own serialized paths.
/// </summary>
internal sealed class BridgeAudioTranscoder
{
    private const int TapSampleRate = 8_000;

    private readonly PayloadCodecKind _wireKind;
    private readonly OpusPayloadCodec? _opus;
    private readonly ILogger _logger;

    private BridgeAudioTranscoder(PayloadCodecKind wireKind, byte wirePayloadType, ILogger logger)
    {
        _wireKind = wireKind;
        WirePayloadType = wirePayloadType;
        _logger = logger;
        _opus = wireKind == PayloadCodecKind.Opus ? new OpusPayloadCodec(TapSampleRate) : null;
    }

    /// <summary>RTP payload type used on the wire (what outbound packets carry).</summary>
    public byte WirePayloadType { get; }

    /// <summary>RTP payload type presented to the bridge — always PCMU (static PT 0).</summary>
    public byte TapPayloadType => 0;

    /// <summary>
    /// Builds a transcoder for a µ-law bridge tap over the given wire codec, or returns
    /// <see langword="null"/> when no transcoding is needed (wire is already µ-law) or when
    /// the wire codec cannot be bridged to µ-law without a resampler (logged once).
    /// </summary>
    public static BridgeAudioTranscoder? CreateForPcmuTap(
        PayloadCodecKind wireKind,
        byte wirePayloadType,
        ILogger logger)
    {
        switch (wireKind)
        {
            case PayloadCodecKind.Pcmu:
                return null; // Wire already matches the tap — plain passthrough, no transcode.

            case PayloadCodecKind.Opus:
            case PayloadCodecKind.Pcma:
                logger.LogInformation(
                    "Bridge audio transcoding enabled: wire {WireKind} (PT {Pt}) <-> tap PCMU.",
                    wireKind, wirePayloadType);
                return new BridgeAudioTranscoder(wireKind, wirePayloadType, logger);

            default:
                logger.LogWarning(
                    "Bridge PCMU tap requested but wire codec {WireKind} cannot be transcoded to µ-law "
                    + "without a resampler; delivering raw payload (bridge audio will be incorrect).",
                    wireKind);
                return null;
        }
    }

    /// <summary>Converts one inbound wire payload into a µ-law tap payload.</summary>
    public byte[] WireToTap(ReadOnlySpan<byte> wirePayload)
    {
        if (wirePayload.Length == 0)
            return [];

        return _wireKind switch
        {
            PayloadCodecKind.Opus => PcmG711Codec.EncodeMuLaw(_opus!.Decode(wirePayload)),
            PayloadCodecKind.Pcma => PcmG711Codec.EncodeMuLaw(PcmG711Codec.DecodeALaw(wirePayload)),
            _ => wirePayload.ToArray()
        };
    }

    /// <summary>Converts one outbound µ-law tap payload into a wire payload.</summary>
    public byte[] TapToWire(ReadOnlySpan<byte> muLawPayload)
    {
        if (muLawPayload.Length == 0)
            return [];

        return _wireKind switch
        {
            PayloadCodecKind.Opus => _opus!.Encode(PcmG711Codec.DecodeMuLaw(muLawPayload)),
            PayloadCodecKind.Pcma => PcmG711Codec.EncodeALaw(PcmG711Codec.DecodeMuLaw(muLawPayload)),
            _ => muLawPayload.ToArray()
        };
    }
}
