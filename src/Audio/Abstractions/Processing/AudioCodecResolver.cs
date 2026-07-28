namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Resolves the <see cref="ActiveCodec"/> for a call leg from the negotiated RTP payload type, the
/// SDP codec name and the payload-type→codec map, and reports the codec's PCM sample rate. Shared by
/// the platform audio devices, which previously duplicated this resolution logic byte-for-byte
/// (issue #18, A8).
/// </summary>
public static class AudioCodecResolver
{
    // Opus RTP clock rate in Hz (RFC 7587 §4.1). Mirrors the internal
    // OpusPayloadCodec.RtpClockRate constant the platform devices used before A8; kept as a local
    // constant so this plattform-neutral helper does not depend on a Core-internal type.
    private const int OpusRtpClockRate = 48_000;

    /// <summary>
    /// Resolves the active codec, preferring an explicit codec name, then the payload-type→codec map,
    /// then well-known static payload types (9/≥16 kHz → G.722, 8 → PCMA, otherwise PCMU).
    /// </summary>
    /// <param name="payloadType">The negotiated RTP payload type.</param>
    /// <param name="sampleRate">The negotiated sample rate in Hz (used to disambiguate wide-band).</param>
    /// <param name="codecName">The negotiated codec name, or an empty string when none was supplied.</param>
    /// <param name="payloadTypeCodecMap">The SDP payload-type→codec-name map for dynamic payload types.</param>
    /// <returns>The resolved <see cref="ActiveCodec"/>.</returns>
    public static ActiveCodec ResolveActiveCodec(
        int payloadType,
        int sampleRate,
        string codecName,
        IReadOnlyDictionary<int, string> payloadTypeCodecMap)
    {
        ArgumentNullException.ThrowIfNull(payloadTypeCodecMap);

        if (MapCodecNameToActiveCodec(codecName) is { } named)
            return named;

        if (payloadTypeCodecMap.TryGetValue(payloadType, out var mapped)
            && MapCodecNameToActiveCodec(mapped) is { } mappedCodec)
        {
            return mappedCodec;
        }

        if (payloadType == 9 || sampleRate >= 16000)
            return ActiveCodec.G722;
        if (payloadType == 8)
            return ActiveCodec.Pcma;
        return ActiveCodec.Pcmu;
    }

    /// <summary>
    /// Maps an SDP codec name (case- and separator-insensitive) to an <see cref="ActiveCodec"/>, or
    /// <see langword="null"/> when the name is blank or unrecognised.
    /// </summary>
    /// <param name="codecName">The codec name to map.</param>
    /// <returns>The mapped codec, or <see langword="null"/> when the name is unknown.</returns>
    public static ActiveCodec? MapCodecNameToActiveCodec(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
            return null;

        return codecName.Trim().ToUpperInvariant() switch
        {
            "G722" or "G.722" => ActiveCodec.G722,
            "PCMA" or "A-LAW" or "A_LAW" => ActiveCodec.Pcma,
            "PCMU" or "MU-LAW" or "MU_LAW" => ActiveCodec.Pcmu,
            "OPUS" => ActiveCodec.Opus,
            _ => null
        };
    }

    /// <summary>
    /// Returns the PCM sample rate in Hz for <paramref name="codec"/>: 48 kHz for Opus, 16 kHz for
    /// G.722, and 8 kHz for the G.711 variants.
    /// </summary>
    /// <param name="codec">The codec whose sample rate is requested.</param>
    /// <returns>The codec's PCM sample rate in Hz.</returns>
    public static int GetCodecSampleRate(ActiveCodec codec) => codec switch
    {
        ActiveCodec.Opus => OpusRtpClockRate, // 48 kHz
        ActiveCodec.G722 => 16_000,
        _ => 8_000
    };
}
