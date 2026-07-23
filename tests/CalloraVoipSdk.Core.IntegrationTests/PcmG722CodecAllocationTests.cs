using CalloraVoipSdk.Core.Application.Media.Sessions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The G.722 transcode codec reuses its intermediate byte[]/short[] scratch buffers across frames
/// (RAM-efficient media path): only the returned array is allocated per call. The previous
/// implementation allocated the materialised input copy (<c>payload.ToArray()</c> /
/// <c>pcm16.ToArray()</c>) plus the intermediate samples array on every frame. These tests measure
/// steady-state per-frame allocation and assert it stays near just the returned array — well below the
/// old per-frame overhead. Correctness (byte-identical output) is covered by <see cref="PcmG722CodecStateTests"/>.
/// </summary>
public sealed class PcmG722CodecAllocationTests
{
    private const int SamplesPerFrame = 320;                 // 20 ms G.722 frame
    private const int PcmBytesPerFrame = SamplesPerFrame * 2; // 640 B PCM16
    private const int G722BytesPerFrame = SamplesPerFrame / 2; // 160 B encoded
    private const int Frames = 20_000;

    private static byte[] PcmFrame()
    {
        var pcm = new byte[PcmBytesPerFrame];
        for (var i = 0; i < SamplesPerFrame; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 12000);
            pcm[i * 2] = (byte)value;
            pcm[i * 2 + 1] = (byte)(value >> 8);
        }
        return pcm;
    }

    private static long MeasureAllocation(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Encode_reuses_intermediate_buffers_across_frames()
    {
        var codec = new PcmG722Codec();
        var frame = PcmFrame();

        // Warm up the JIT and let the scratch buffer grow to frame size once.
        for (var i = 0; i < 200; i++)
            _ = codec.Encode(frame);

        var allocated = MeasureAllocation(() =>
        {
            for (var i = 0; i < Frames; i++)
                _ = codec.Encode(frame);
        });

        // Steady state allocates only the returned 160 B array (plus object header). The old path also
        // allocated the 640 B ToArray() copy and the 640 B samples array per frame, so 400 B/frame sits
        // safely between "returned array only" (~184 B) and the old overhead (~1.4 KB).
        var perFrame = allocated / (double)Frames;
        Assert.True(
            perFrame < 400,
            $"Encode allocated {perFrame:F0} B/frame (returned {G722BytesPerFrame} B array); intermediates not reused.");
    }

    [Fact]
    public void Decode_reuses_intermediate_buffers_across_frames()
    {
        var codec = new PcmG722Codec();
        // A realistic G.722 frame produced by the codec itself.
        var g722 = codec.Encode(PcmFrame());
        Assert.Equal(G722BytesPerFrame, g722.Length);

        var decoder = new PcmG722Codec();
        for (var i = 0; i < 200; i++)
            _ = decoder.Decode(g722);

        var allocated = MeasureAllocation(() =>
        {
            for (var i = 0; i < Frames; i++)
                _ = decoder.Decode(g722);
        });

        // Steady state allocates only the returned 640 B PCM array. The old path also allocated the
        // 160 B input copy and the 640 B samples array per frame, so 900 B/frame sits between
        // "returned array only" (~664 B) and the old overhead (~1.5 KB).
        var perFrame = allocated / (double)Frames;
        Assert.True(
            perFrame < 900,
            $"Decode allocated {perFrame:F0} B/frame (returned {PcmBytesPerFrame} B array); intermediates not reused.");
    }
}
