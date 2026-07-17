using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// Equivalence gate for the shared in-place PCM gain helper (HARD-F1). <see cref="PcmGain.ApplyInPlace"/>
/// replaced two per-device copies of an allocating <c>ApplyGain</c>; it must produce the exact same
/// samples for every case (mute, unity, scaled, clamped) while mutating the caller's buffer instead of
/// allocating a new one.
/// </summary>
public sealed class PcmGainTests
{
    // The previous allocating semantics, as a reference oracle.
    private static byte[] LegacyApplyGain(byte[] pcm, bool muted, float volume)
    {
        if (pcm.Length == 0)
            return pcm;
        if (muted || volume <= 0f)
            return new byte[pcm.Length];
        if (Math.Abs(volume - 1f) < 0.0001f)
            return pcm;

        var adjusted = new byte[pcm.Length];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (int)Math.Round(sample * volume, MidpointRounding.AwayFromZero);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            adjusted[i] = (byte)(scaled & 0xFF);
            adjusted[i + 1] = (byte)(scaled >> 8);
        }
        return adjusted;
    }

    [Theory]
    [InlineData(false, 1.0f)]   // unity — unchanged
    [InlineData(true, 1.0f)]    // muted — zeroed
    [InlineData(false, 0.0f)]   // zero volume — zeroed
    [InlineData(false, 0.5f)]   // attenuate
    [InlineData(false, 2.5f)]   // amplify + clamp
    [InlineData(false, 8.0f)]   // heavy amplify — clamps to the rails
    public void ApplyInPlace_matches_the_legacy_allocating_gain(bool muted, float volume)
    {
        var source = Pcm();
        var expected = LegacyApplyGain((byte[])source.Clone(), muted, volume);

        var actual = PcmGain.ApplyInPlace(source, muted, volume);

        Assert.Equal(expected, actual);
        Assert.Same(source, actual); // mutated in place, no new allocation
    }

    // A spread of 16-bit LE samples including extremes so scaling and clamping are exercised.
    private static byte[] Pcm()
    {
        short[] samples = [0, 1, -1, 100, -100, 12000, -12000, short.MaxValue, short.MinValue, 32000];
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            pcm[i * 2] = (byte)(samples[i] & 0xFF);
            pcm[i * 2 + 1] = (byte)(samples[i] >> 8);
        }
        return pcm;
    }
}
