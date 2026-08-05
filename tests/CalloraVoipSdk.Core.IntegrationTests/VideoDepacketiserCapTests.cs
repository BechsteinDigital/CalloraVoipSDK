using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #161 RTP P1: the H.264/VP8 depacketisers reassembled into unbounded MemoryStreams, so a same-timestamp
/// markerless run (or a never-terminated FU-A) grew without limit per track/RID lane — a wire DoS even after
/// SRTP authentication (K4). These tests pin the hard reassembly cap: an over-limit frame is fully discarded,
/// counted, and does not desync the next frame.
/// </summary>
public sealed class VideoDepacketiserCapTests
{
    private const int Cap = 64;

    // ── payload builders ───────────────────────────────────────────────────────

    private static byte[] Vp8(bool frameStart, int dataLen)
    {
        var p = new byte[1 + dataLen];
        p[0] = (byte)(frameStart ? 0x10 : 0x00); // S=1/PID=0 starts a frame; no extension bit
        return p;
    }

    private static byte[] Nal(int type, int dataLen)
    {
        var p = new byte[1 + dataLen];
        p[0] = (byte)(0x60 | (type & 0x1F)); // F=0, NRI=3, NAL type
        return p;
    }

    private static byte[] FuA(bool start, bool end, int dataLen)
    {
        var p = new byte[2 + dataLen];
        p[0] = 0x7C;                                                 // FU indicator: F=0, NRI=3, type 28
        p[1] = (byte)((start ? 0x80 : 0) | (end ? 0x40 : 0) | 0x05); // FU header: S/E + inner type 5 (IDR)
        return p;
    }

    private static byte[] StapA(int unitLen, int count)
    {
        var p = new byte[1 + count * (2 + unitLen)];
        p[0] = 0x78; // STAP-A NAL header (F=0, NRI=3, type 24)
        var offset = 1;
        for (var i = 0; i < count; i++)
        {
            p[offset] = (byte)(unitLen >> 8);
            p[offset + 1] = (byte)unitLen;
            offset += 2 + unitLen;
        }
        return p;
    }

    // ── VP8 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Vp8_markerless_run_over_the_cap_is_discarded_and_does_not_desync_the_next_frame()
    {
        var d = new Vp8Depacketiser(Cap);

        Assert.False(d.TryProcess(Vp8(frameStart: true, dataLen: 40), 1, marker: false, out _, out _));
        Assert.False(d.TryProcess(Vp8(frameStart: false, dataLen: 40), 1, marker: false, out var over, out _)); // 80 > 64
        Assert.Null(over);
        Assert.Equal(1, d.OversizedFrameDiscardCount);

        // A fresh frame (new timestamp) reassembles cleanly — the over-limit frame did not desync it.
        Assert.False(d.TryProcess(Vp8(frameStart: true, dataLen: 10), 2, marker: false, out _, out _));
        Assert.True(d.TryProcess(Vp8(frameStart: false, dataLen: 10), 2, marker: true, out var frame, out _));
        Assert.NotNull(frame);
        Assert.Equal(20, frame!.Length);
    }

    // ── H.264 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void H264_single_nal_run_over_the_cap_is_discarded()
    {
        var d = new H264Depacketiser(Cap);

        Assert.False(d.TryProcess(Nal(type: 1, dataLen: 40), 1, marker: false, out _, out _)); // frame = 45
        Assert.False(d.TryProcess(Nal(type: 1, dataLen: 40), 1, marker: false, out var over, out _)); // + 45 > 64
        Assert.Null(over);
        Assert.Equal(1, d.OversizedFrameDiscardCount);
    }

    [Fact]
    public void H264_stap_a_over_the_cap_is_discarded()
    {
        var d = new H264Depacketiser(Cap);

        // Two 30-byte units → 34 + 34 = 68 > 64 across the start codes.
        Assert.False(d.TryProcess(StapA(unitLen: 30, count: 2), 1, marker: false, out var over, out _));
        Assert.Null(over);
        Assert.Equal(1, d.OversizedFrameDiscardCount);
    }

    [Fact]
    public void H264_never_terminated_fu_a_over_the_cap_is_discarded_and_does_not_desync()
    {
        var d = new H264Depacketiser(Cap);

        Assert.False(d.TryProcess(FuA(start: true, end: false, dataLen: 30), 1, marker: false, out _, out _)); // fragment ~31
        Assert.False(d.TryProcess(FuA(start: false, end: false, dataLen: 40), 1, marker: false, out var over, out _)); // 71 > 64
        Assert.Null(over);
        Assert.Equal(1, d.OversizedFrameDiscardCount);

        // A fresh single-NAL frame reassembles after the aborted fragment run.
        Assert.True(d.TryProcess(Nal(type: 5, dataLen: 10), 2, marker: true, out var frame, out var isKey));
        Assert.NotNull(frame);
        Assert.True(isKey); // NAL type 5 = IDR
    }

    [Fact]
    public void A_frame_within_the_cap_still_completes()
    {
        var d = new Vp8Depacketiser(Cap);

        Assert.True(d.TryProcess(Vp8(frameStart: true, dataLen: 30), 1, marker: true, out var frame, out _));
        Assert.NotNull(frame);
        Assert.Equal(0, d.OversizedFrameDiscardCount);
    }

    [Fact]
    public void A_non_positive_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8Depacketiser(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Depacketiser(-1));
    }
}
