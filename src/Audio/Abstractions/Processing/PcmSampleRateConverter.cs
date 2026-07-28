namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Converts a 16-bit little-endian PCM frame between sample rates on the audio hotpath using
/// nearest-neighbour resampling. Shared by the platform audio devices, which previously duplicated
/// this logic byte-for-byte (issue #18, A8).
/// </summary>
/// <remarks>
/// Nearest-neighbour resampling is aliasing-prone; it is retained verbatim from the per-device
/// implementations to keep A8 a behaviour-preserving refactoring. Replacing it with a filtered
/// resampler is tracked as follow-up work, not part of this extraction.
/// </remarks>
public static class PcmSampleRateConverter
{
    /// <summary>
    /// Resamples <paramref name="pcm"/> from <paramref name="sourceSampleRate"/> to
    /// <paramref name="targetSampleRate"/> via nearest-neighbour selection. An empty buffer, a
    /// non-positive rate on either side, or matching rates return <paramref name="pcm"/> unchanged.
    /// </summary>
    /// <param name="pcm">The 16-bit little-endian PCM frame to convert.</param>
    /// <param name="sourceSampleRate">The source sample rate in Hz.</param>
    /// <param name="targetSampleRate">The target sample rate in Hz.</param>
    /// <returns>The resampled frame, or the input buffer when no conversion is required.</returns>
    public static byte[] ConvertPcmSampleRate(byte[] pcm, int sourceSampleRate, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        if (pcm.Length == 0)
            return pcm;
        if (sourceSampleRate <= 0 || targetSampleRate <= 0)
            return pcm;
        if (sourceSampleRate == targetSampleRate)
            return pcm;

        var sourceSamples = pcm.Length / 2;
        if (sourceSamples == 0)
            return Array.Empty<byte>();

        var targetSamples = Math.Max(
            1,
            (int)Math.Round(
                sourceSamples * (double)targetSampleRate / sourceSampleRate,
                MidpointRounding.AwayFromZero));

        var converted = new byte[targetSamples * 2];
        for (var i = 0; i < targetSamples; i++)
        {
            var sourceIndex = (int)Math.Min(sourceSamples - 1, (long)i * sourceSampleRate / targetSampleRate);
            converted[i * 2] = pcm[sourceIndex * 2];
            converted[i * 2 + 1] = pcm[sourceIndex * 2 + 1];
        }

        return converted;
    }
}
