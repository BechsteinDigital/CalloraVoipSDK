namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Applies mute/volume to a 16-bit little-endian PCM frame in place on the audio hotpath. Scaling the
/// caller's own frame buffer avoids the per-frame output allocation the previous per-device helpers
/// made (HARD-F1). Shared by the platform audio devices, which previously duplicated this logic.
/// </summary>
public static class PcmGain
{
    // Volumes within this tolerance of 1.0 are treated as unity and leave the buffer untouched.
    private const float UnityTolerance = 0.0001f;

    /// <summary>
    /// Applies mute and volume to <paramref name="pcm"/> in place: a muted or non-positive volume
    /// zeroes the buffer, a unity volume leaves it unchanged, and any other volume scales and clamps
    /// each sample. The caller must own the buffer exclusively (it is mutated). Returns
    /// <paramref name="pcm"/> for call-site convenience.
    /// </summary>
    public static byte[] ApplyInPlace(byte[] pcm, bool muted, float volume)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        if (pcm.Length == 0)
            return pcm;

        if (muted || volume <= 0f)
        {
            Array.Clear(pcm);
            return pcm;
        }

        if (Math.Abs(volume - 1f) < UnityTolerance)
            return pcm;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (int)Math.Round(sample * volume, MidpointRounding.AwayFromZero);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            pcm[i] = (byte)(scaled & 0xFF);
            pcm[i + 1] = (byte)(scaled >> 8);
        }

        return pcm;
    }
}
