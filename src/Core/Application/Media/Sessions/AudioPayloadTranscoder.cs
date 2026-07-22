using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media.Sessions;

/// <summary>
/// Builds payload transcoding plans between runtime media frames and file codecs.
/// </summary>
internal static class AudioPayloadTranscoder
{
    private const string Mp3PassthroughCodecName = "MP3-PASSTHROUGH";

    /// <summary>
    /// Resolves a transcoding plan for one call target.
    /// </summary>
    public static bool TryCreateForCall(
        ICall call,
        AudioFileFormat fileFormat,
        int fallbackSampleRate,
        int fallbackSamplesPerFrame,
        out AudioPayloadTranscodingPlan? plan,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(call);

        var media = call.MediaParameters;
        if (media is null)
        {
            plan = null;
            error = "Call media parameters are not negotiated yet.";
            return false;
        }

        var payloadType = media.PayloadType;
        var clockRate = media.ClockRate > 0 ? media.ClockRate : fallbackSampleRate;
        var samplesPerFrame = media.SamplesPerPacket > 0 ? media.SamplesPerPacket : fallbackSamplesPerFrame;
        var codecName = ResolveCodecName(media);
        var codecKind = ResolveCodecKind(codecName, payloadType);

        return fileFormat switch
        {
            AudioFileFormat.Wav => TryCreatePcmFilePlanForCall(
                codecKind,
                payloadType,
                clockRate,
                samplesPerFrame,
                codecName,
                out plan,
                out error),

            AudioFileFormat.Mp3 => TryCreateMp3PlanForCall(
                codecKind,
                payloadType,
                clockRate,
                samplesPerFrame,
                codecName,
                out plan,
                out error),

            _ => UnsupportedFileFormat(fileFormat, out plan, out error)
        };
    }

    /// <summary>
    /// Resolves a transcoding plan for conference mix payloads.
    /// </summary>
    public static bool TryCreateForConference(
        AudioFileFormat fileFormat,
        int sampleRate,
        int samplesPerFrame,
        out AudioPayloadTranscodingPlan? plan,
        out string error)
    {
        if (fileFormat is not (AudioFileFormat.Wav or AudioFileFormat.Mp3))
            return UnsupportedFileFormat(fileFormat, out plan, out error);

        var safeSampleRate = sampleRate > 0 ? sampleRate : 8000;
        var safeSamplesPerFrame = samplesPerFrame > 0 ? samplesPerFrame : 160;

        var context = new AudioFileCodecContext(
            PayloadType: 0,
            ClockRate: safeSampleRate,
            SampleRate: safeSampleRate,
            SamplesPerFrame: safeSamplesPerFrame,
            CodecName: "L16");

        plan = new AudioPayloadTranscodingPlan(
            context,
            toFileFrame: frame =>
            {
                EnsurePcm16(frame.Payload.Span);
                return frame;
            },
            fromFileFrame: frame =>
            {
                EnsurePcm16(frame.Payload.Span);
                return frame;
            });

        error = string.Empty;
        return true;
    }

    private static bool TryCreateMp3PlanForCall(
        PayloadCodecKind codecKind,
        int payloadType,
        int clockRate,
        int samplesPerFrame,
        string codecName,
        out AudioPayloadTranscodingPlan? plan,
        out string error)
    {
        if (codecKind == PayloadCodecKind.Mp3)
        {
            var passthroughContext = new AudioFileCodecContext(
                PayloadType: payloadType,
                ClockRate: clockRate,
                SampleRate: clockRate,
                SamplesPerFrame: Math.Max(samplesPerFrame, 1),
                CodecName: Mp3PassthroughCodecName);

            plan = new AudioPayloadTranscodingPlan(
                passthroughContext,
                toFileFrame: frame => new MediaFrame(frame.Payload, payloadType, frame.DurationRtpUnits),
                fromFileFrame: frame => new MediaFrame(frame.Payload, payloadType, frame.DurationRtpUnits));

            error = string.Empty;
            return true;
        }

        return TryCreatePcmFilePlanForCall(
            codecKind,
            payloadType,
            clockRate,
            samplesPerFrame,
            codecName,
            out plan,
            out error);
    }

    internal static bool TryCreatePcmFilePlanForCall(
        PayloadCodecKind codecKind,
        int payloadType,
        int clockRate,
        int samplesPerFrame,
        string codecName,
        out AudioPayloadTranscodingPlan? plan,
        out string error)
    {
        var context = new AudioFileCodecContext(
            PayloadType: payloadType,
            ClockRate: clockRate,
            SampleRate: codecKind == PayloadCodecKind.G722 ? 16_000 : clockRate,
            SamplesPerFrame: Math.Max(samplesPerFrame, 1),
            CodecName: "L16");

        switch (codecKind)
        {
            case PayloadCodecKind.Pcm16:
                plan = new AudioPayloadTranscodingPlan(
                    context,
                    toFileFrame: frame =>
                    {
                        EnsurePcm16(frame.Payload.Span);
                        return frame;
                    },
                    fromFileFrame: frame =>
                    {
                        EnsurePcm16(frame.Payload.Span);
                        return new MediaFrame(frame.Payload, payloadType, frame.DurationRtpUnits);
                    });
                error = string.Empty;
                return true;

            case PayloadCodecKind.Pcmu:
                plan = new AudioPayloadTranscodingPlan(
                    context,
                    toFileFrame: frame =>
                    {
                        var decoded = PcmG711Codec.DecodeMuLaw(frame.Payload.Span);
                        return new MediaFrame(decoded, payloadType, frame.DurationRtpUnits);
                    },
                    fromFileFrame: frame =>
                    {
                        EnsurePcm16(frame.Payload.Span);
                        var encoded = PcmG711Codec.EncodeMuLaw(frame.Payload.Span);
                        return new MediaFrame(encoded, payloadType, frame.DurationRtpUnits);
                    });
                error = string.Empty;
                return true;

            case PayloadCodecKind.Pcma:
                plan = new AudioPayloadTranscodingPlan(
                    context,
                    toFileFrame: frame =>
                    {
                        var decoded = PcmG711Codec.DecodeALaw(frame.Payload.Span);
                        return new MediaFrame(decoded, payloadType, frame.DurationRtpUnits);
                    },
                    fromFileFrame: frame =>
                    {
                        EnsurePcm16(frame.Payload.Span);
                        var encoded = PcmG711Codec.EncodeALaw(frame.Payload.Span);
                        return new MediaFrame(encoded, payloadType, frame.DurationRtpUnits);
                    });
                error = string.Empty;
                return true;

            case PayloadCodecKind.G722:
            {
                // G.722 is ADPCM: the predictor state carries across frame boundaries, so the plan owns
                // one codec instance (a persistent decode state + a persistent encode state) for the
                // lifetime of this call leg — re-initialising per frame produces audible artefacts.
                var g722 = new PcmG722Codec();
                plan = new AudioPayloadTranscodingPlan(
                    context,
                    toFileFrame: frame =>
                    {
                        var decoded = g722.Decode(frame.Payload.Span);
                        return new MediaFrame(decoded, payloadType, frame.DurationRtpUnits);
                    },
                    fromFileFrame: frame =>
                    {
                        EnsurePcm16(frame.Payload.Span);
                        var encoded = g722.Encode(frame.Payload.Span);
                        return new MediaFrame(encoded, payloadType, frame.DurationRtpUnits);
                    });
                error = string.Empty;
                return true;
            }

            case PayloadCodecKind.Opus:
            {
                // Opus is stateful across frames (prediction/FEC); the plan owns one
                // decoder and one encoder instance for the lifetime of this call leg.
                var opus = new OpusPayloadCodec();
                plan = new AudioPayloadTranscodingPlan(
                    context,
                    toFileFrame: frame =>
                    {
                        var decoded = opus.Decode(frame.Payload.Span);
                        return new MediaFrame(decoded, payloadType, frame.DurationRtpUnits);
                    },
                    fromFileFrame: frame =>
                    {
                        EnsurePcm16(frame.Payload.Span);
                        var encoded = opus.Encode(frame.Payload.Span);
                        return new MediaFrame(encoded, payloadType, frame.DurationRtpUnits);
                    });
                error = string.Empty;
                return true;
            }

            default:
                plan = null;
                error = $"Transcoding is not supported for codec '{codecName}'.";
                return false;
        }
    }

    private static bool UnsupportedFileFormat(
        AudioFileFormat fileFormat,
        out AudioPayloadTranscodingPlan? plan,
        out string error)
    {
        plan = null;
        error = $"Unsupported file format '{fileFormat}'.";
        return false;
    }

    private static string ResolveCodecName(CallMediaParameters media)
    {
        if (!string.IsNullOrWhiteSpace(media.CodecName))
            return media.CodecName.Trim();

        if (media.PayloadTypeCodecMap.TryGetValue(media.PayloadType, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim();
        }

        return media.PayloadType switch
        {
            0 => "PCMU",
            8 => "PCMA",
            _ => "UNKNOWN"
        };
    }

    internal static PayloadCodecKind ResolveCodecKind(string codecName, int payloadType)
    {
        var normalized = codecName.Trim().ToUpperInvariant();
        if (normalized.Contains("PCMU", StringComparison.Ordinal) ||
            normalized.Contains("MU-LAW", StringComparison.Ordinal) ||
            normalized.Contains("MULAW", StringComparison.Ordinal))
        {
            return PayloadCodecKind.Pcmu;
        }

        if (normalized.Contains("PCMA", StringComparison.Ordinal) ||
            normalized.Contains("A-LAW", StringComparison.Ordinal) ||
            normalized.Contains("ALAW", StringComparison.Ordinal))
        {
            return PayloadCodecKind.Pcma;
        }

        if (normalized.Contains("MP3", StringComparison.Ordinal) ||
            normalized.Contains("MPEG", StringComparison.Ordinal) ||
            normalized.Contains("MPA", StringComparison.Ordinal))
        {
            return PayloadCodecKind.Mp3;
        }

        if (normalized.Contains("L16", StringComparison.Ordinal) ||
            normalized.Contains("PCM", StringComparison.Ordinal))
        {
            return PayloadCodecKind.Pcm16;
        }

        if (normalized.Contains("G722", StringComparison.Ordinal) ||
            normalized.Contains("G.722", StringComparison.Ordinal))
        {
            return PayloadCodecKind.G722;
        }

        if (normalized.Contains("OPUS", StringComparison.Ordinal))
            return PayloadCodecKind.Opus;

        if (normalized.Contains("CN", StringComparison.Ordinal) ||
            normalized.Contains("COMFORT-NOISE", StringComparison.Ordinal))
        {
            return PayloadCodecKind.ComfortNoise;
        }

        return payloadType switch
        {
            0 => PayloadCodecKind.Pcmu,
            8 => PayloadCodecKind.Pcma,
            9 => PayloadCodecKind.G722,
            13 => PayloadCodecKind.ComfortNoise,
            _ => PayloadCodecKind.Unknown,
        };
    }

    private static void EnsurePcm16(ReadOnlySpan<byte> payload)
    {
        if ((payload.Length & 1) != 0)
            throw new InvalidOperationException("PCM16 payload length must be even.");
    }
}
