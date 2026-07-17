using NAudio.Codecs;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// Evidence for caching the G722 codec instance on the audio hotpath (HARD-F1). NAudio's
/// <see cref="G722Codec"/> keeps no per-instance state — the codec state lives in
/// <see cref="G722CodecState"/> — so reusing one instance across frames is behaviour-identical to
/// allocating a fresh one per frame, while removing a per-frame heap allocation.
/// </summary>
public sealed class G722CodecCachingTests
{
    // 20 ms G722 frame: 320 wideband 16-bit samples encode to 160 bytes.
    private const int SamplesPerFrame = 320;
    private const int EncodedBytesPerFrame = SamplesPerFrame / 2;

    private static short[] Frame(int seed)
    {
        var samples = new short[SamplesPerFrame];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)((i * 31 + seed * 7) & 0x7FFF);
        return samples;
    }

    [Fact]
    public void Cached_instance_encodes_identically_to_a_fresh_instance_per_frame()
    {
        const int frames = 50;
        var freshState = new G722CodecState(64000, G722Flags.None);
        var cachedState = new G722CodecState(64000, G722Flags.None);
        var cachedCodec = new G722Codec();

        for (var f = 0; f < frames; f++)
        {
            var samples = Frame(f);
            var fresh = new byte[EncodedBytesPerFrame];
            var cached = new byte[EncodedBytesPerFrame];

            new G722Codec().Encode(freshState, fresh, samples, samples.Length);  // pre-F1 behaviour
            cachedCodec.Encode(cachedState, cached, samples, samples.Length);     // HARD-F1 behaviour

            // Byte-identical across the whole sequence proves the cached instance carries no state
            // that a fresh-per-frame instance would have reset.
            Assert.Equal(fresh, cached);
        }
    }

    [Fact]
    public void Cached_instance_allocates_less_than_a_fresh_instance_per_frame()
    {
        const int frames = 20_000;
        var samples = Frame(1);
        var encoded = new byte[EncodedBytesPerFrame];

        // Warm up the JIT so the measurement reflects steady-state allocation.
        Encode(freshPerFrame: true, samples, encoded, 200);
        Encode(freshPerFrame: false, samples, encoded, 200);

        var freshBytes = MeasureAllocation(() => Encode(freshPerFrame: true, samples, encoded, frames));
        var cachedBytes = MeasureAllocation(() => Encode(freshPerFrame: false, samples, encoded, frames));

        // The pre-F1 path allocates one G722Codec per frame; the cached path allocates none.
        Assert.True(
            cachedBytes < freshBytes,
            $"Expected cached ({cachedBytes} B) < fresh-per-frame ({freshBytes} B) over {frames} frames.");
    }

    private static void Encode(bool freshPerFrame, short[] samples, byte[] encoded, int frames)
    {
        var state = new G722CodecState(64000, G722Flags.None);
        var cached = freshPerFrame ? null : new G722Codec();
        for (var i = 0; i < frames; i++)
            (freshPerFrame ? new G722Codec() : cached!).Encode(state, encoded, samples, samples.Length);
    }

    private static long MeasureAllocation(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
