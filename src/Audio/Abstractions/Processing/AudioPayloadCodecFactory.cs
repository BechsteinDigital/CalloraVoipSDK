namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Creates <see cref="IAudioPayloadCodec"/> instances that wrap the SDK's built-in payload codecs
/// (Opus/G.711/G.722) for server-side PCM transcoding (#205). Each returned instance is stateful and
/// single-direction — see <see cref="IAudioPayloadCodec"/> — so create one per stream direction.
/// </summary>
public static class AudioPayloadCodecFactory
{
    // The Opus PCM sample rates Concentus supports (RFC 7587 §4.1 permits 8/12/16/24/48 kHz).
    private static readonly int[] SupportedOpusRates = [8_000, 12_000, 16_000, 24_000, 48_000];

    /// <summary>
    /// Creates a transcoder for <paramref name="codec"/> at its canonical PCM sample rate
    /// (<see cref="AudioCodecResolver.GetCodecSampleRate"/>): 48 kHz for Opus, 16 kHz for G.722, 8 kHz for
    /// the G.711 variants.
    /// </summary>
    /// <param name="codec">The codec to transcode.</param>
    /// <returns>A new single-direction transcoder instance.</returns>
    public static IAudioPayloadCodec Create(ActiveCodec codec)
        => Create(codec, AudioCodecResolver.GetCodecSampleRate(codec));

    /// <summary>
    /// Creates a transcoder for <paramref name="codec"/> at an explicit PCM sample rate. Opus honours any
    /// supported rate (8/12/16/24/48 kHz) — Concentus resamples internally, and the RTP timestamp clock stays
    /// 48 kHz regardless (RFC 7587 §4.1). G.711 and G.722 are fixed-rate: <paramref name="pcmSampleRate"/>
    /// must equal their canonical rate (8 kHz / 16 kHz), otherwise the call fails closed rather than
    /// producing silently mis-rated audio.
    /// </summary>
    /// <param name="codec">The codec to transcode.</param>
    /// <param name="pcmSampleRate">The PCM sample rate in Hz the instance decodes to and encodes from.</param>
    /// <returns>A new single-direction transcoder instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pcmSampleRate"/> is not positive, or is unsupported for Opus.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcmSampleRate"/> is not the fixed rate of a G.711/G.722 codec, or <paramref name="codec"/> is unknown.</exception>
    public static IAudioPayloadCodec Create(ActiveCodec codec, int pcmSampleRate)
    {
        if (pcmSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(pcmSampleRate), pcmSampleRate, "The PCM sample rate must be positive.");

        switch (codec)
        {
            case ActiveCodec.Opus:
                if (Array.IndexOf(SupportedOpusRates, pcmSampleRate) < 0)
                    throw new ArgumentOutOfRangeException(
                        nameof(pcmSampleRate), pcmSampleRate,
                        "Opus supports PCM sample rates 8000, 12000, 16000, 24000 or 48000 Hz (RFC 7587).");
                return new OpusAudioPayloadCodec(pcmSampleRate);

            case ActiveCodec.G722:
                if (pcmSampleRate != 16_000)
                    throw new ArgumentException(
                        $"G.722 is a fixed 16 kHz PCM codec and cannot transcode at {pcmSampleRate} Hz.", nameof(pcmSampleRate));
                return new G722AudioPayloadCodec();

            case ActiveCodec.Pcma:
            case ActiveCodec.Pcmu:
                if (pcmSampleRate != 8_000)
                    throw new ArgumentException(
                        $"{codec} is a fixed 8 kHz PCM codec and cannot transcode at {pcmSampleRate} Hz.", nameof(pcmSampleRate));
                return new G711AudioPayloadCodec(codec);

            default:
                throw new ArgumentException($"Unsupported audio codec '{codec}'.", nameof(codec));
        }
    }
}
