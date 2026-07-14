using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video RTP payload formats (WebRTC phase 2a): H.264 packetisation per RFC 6184
/// (Single NAL / FU-A send, plus STAP-A receive) and VP8 per RFC 7741 — proven by
/// header-level assertions and full packetise→depacketise round trips.
/// </summary>
public sealed class VideoPacketisationTests
{
    // ── H.264: packetiser ───────────────────────────────────────────────────────

    [Fact]
    public void H264_small_nals_travel_as_single_nal_packets()
    {
        var sps = Nal(0x67, 20);
        var pps = Nal(0x68, 6);
        var idr = Nal(0x65, 100);
        var frame = AnnexB((sps, false), (pps, true), (idr, false)); // mixed 3/4-byte start codes

        var payloads = new H264Packetiser().Packetise(frame, maxPayloadSize: 1200);

        Assert.Equal(3, payloads.Count);
        Assert.Equal(sps, payloads[0].Payload.ToArray());
        Assert.Equal(pps, payloads[1].Payload.ToArray());
        Assert.Equal(idr, payloads[2].Payload.ToArray());
        Assert.Equal([false, false, true], payloads.Select(p => p.IsLastOfFrame).ToArray());
    }

    [Fact]
    public void H264_large_nal_fragments_as_fua_with_correct_flags()
    {
        var idr = Nal(0x65, 3000); // NRI=3, type=5
        var payloads = new H264Packetiser().Packetise(AnnexB((idr, false)), maxPayloadSize: 1200);

        Assert.True(payloads.Count >= 3);
        foreach (var p in payloads)
        {
            Assert.True(p.Payload.Length <= 1200);
            Assert.Equal(0x60 | 28, p.Payload.Span[0]); // FU indicator: NRI preserved, type 28
            Assert.Equal(5, p.Payload.Span[1] & 0x1F);  // FU header carries the original type
        }

        Assert.Equal(0x80, payloads[0].Payload.Span[1] & 0xC0);  // S on the first
        Assert.Equal(0x40, payloads[^1].Payload.Span[1] & 0xC0); // E on the last
        Assert.All(payloads.Skip(1).Take(payloads.Count - 2), p => Assert.Equal(0, p.Payload.Span[1] & 0xC0));
        Assert.True(payloads[^1].IsLastOfFrame);
        Assert.Equal(1, payloads.Count(p => p.IsLastOfFrame));
    }

    // ── H.264: round trip and STAP-A receive ────────────────────────────────────

    [Theory]
    [InlineData(300)]   // everything fragments
    [InlineData(1200)]  // SPS/PPS single, IDR fragments
    public void H264_round_trip_reproduces_the_access_unit(int maxPayloadSize)
    {
        var frame = AnnexB((Nal(0x67, 25), false), (Nal(0x68, 8), false), (Nal(0x65, 4000), false));
        var payloads = new H264Packetiser().Packetise(frame, maxPayloadSize);

        var depacketiser = new H264Depacketiser();
        byte[]? rebuilt = null;
        for (var i = 0; i < payloads.Count; i++)
        {
            var done = depacketiser.TryProcess(payloads[i].Payload, 9000, payloads[i].IsLastOfFrame, out rebuilt);
            Assert.Equal(i == payloads.Count - 1, done);
        }

        // The depacketiser normalises to 4-byte start codes; re-parse both sides to
        // compare the NAL units themselves.
        Assert.Equal(
            AnnexBParser.ParseNalUnits(frame).Select(n => n.ToArray()),
            AnnexBParser.ParseNalUnits(rebuilt!).Select(n => n.ToArray()));
    }

    [Fact]
    public void H264_stap_a_aggregation_is_unpacked()
    {
        var sps = Nal(0x67, 10);
        var pps = Nal(0x68, 4);
        var stapA = new byte[1 + 2 + sps.Length + 2 + pps.Length];
        stapA[0] = 24; // STAP-A
        stapA[1] = 0; stapA[2] = (byte)sps.Length;
        sps.CopyTo(stapA, 3);
        stapA[3 + sps.Length] = 0; stapA[4 + sps.Length] = (byte)pps.Length;
        pps.CopyTo(stapA, 5 + sps.Length);

        var depacketiser = new H264Depacketiser();
        Assert.True(depacketiser.TryProcess(stapA, 9000, marker: true, out var frame));
        Assert.Equal(new[] { sps, pps }, AnnexBParser.ParseNalUnits(frame!).Select(n => n.ToArray()));
    }

    // ── H.264: fail-closed on loss and malformed input ──────────────────────────

    [Fact]
    public void H264_lost_fua_start_discards_instead_of_corrupting()
    {
        var payloads = new H264Packetiser().Packetise(AnnexB((Nal(0x65, 3000), false)), 1200);
        var depacketiser = new H264Depacketiser();

        // Feed from the second fragment on — no frame may ever surface.
        foreach (var p in payloads.Skip(1))
            Assert.False(depacketiser.TryProcess(p.Payload, 9000, p.IsLastOfFrame, out _));

        // The next intact frame still assembles after the discard.
        var next = new H264Packetiser().Packetise(AnnexB((Nal(0x61, 50), false)), 1200);
        Assert.True(depacketiser.TryProcess(next[0].Payload, 9020, marker: true, out var frame));
        Assert.NotNull(frame);
    }

    [Theory]
    [InlineData(new byte[] { 24, 0, 200, 1 })]      // STAP-A size beyond payload
    [InlineData(new byte[] { 28 })]                  // FU-A shorter than its header
    [InlineData(new byte[] { 29, 0x85, 1, 2, 3 })]   // FU-B unsupported
    public void H264_malformed_payloads_are_discarded(byte[] payload)
    {
        var depacketiser = new H264Depacketiser();
        Assert.False(depacketiser.TryProcess(payload, 9000, marker: true, out var frame));
        Assert.Null(frame);
    }

    [Fact]
    public void H264_nal_exactly_at_budget_stays_single_nal()
    {
        var nal = Nal(0x65, 1199); // 1200 bytes total == budget
        var payloads = new H264Packetiser().Packetise(AnnexB((nal, false)), maxPayloadSize: 1200);

        Assert.Single(payloads);
        Assert.Equal(nal, payloads[0].Payload.ToArray());
    }

    [Fact]
    public void H264_marker_inside_open_fua_run_discards_the_frame()
    {
        var payloads = new H264Packetiser().Packetise(AnnexB((Nal(0x65, 3000), false)), 1200);
        var depacketiser = new H264Depacketiser();

        // First fragment (S=1) delivered with a lying marker — truncated fragment run.
        Assert.False(depacketiser.TryProcess(payloads[0].Payload, 9000, marker: true, out var frame));
        Assert.Null(frame);
    }

    [Fact]
    public void H264_fua_restart_without_end_discards_the_frame()
    {
        var payloads = new H264Packetiser().Packetise(AnnexB((Nal(0x65, 3000), false)), 1200);
        var depacketiser = new H264Depacketiser();

        Assert.False(depacketiser.TryProcess(payloads[0].Payload, 9000, marker: false, out _));
        // A second S=1 without a closing E is a protocol violation — fail closed.
        Assert.False(depacketiser.TryProcess(payloads[0].Payload, 9000, marker: false, out _));
        Assert.False(depacketiser.TryProcess(payloads[^1].Payload, 9000, marker: true, out var frame));
        Assert.Null(frame);
    }

    [Fact]
    public void H264_timestamp_change_without_marker_never_merges_access_units()
    {
        // Markerless sender: frame 1 is never closed, frame 2 arrives with a new timestamp.
        var first = new H264Packetiser().Packetise(AnnexB((Nal(0x61, 50), false)), 1200);
        var second = new H264Packetiser().Packetise(AnnexB((Nal(0x65, 60), false)), 1200);

        var depacketiser = new H264Depacketiser();
        Assert.False(depacketiser.TryProcess(first[0].Payload, 1000, marker: false, out _));
        Assert.True(depacketiser.TryProcess(second[0].Payload, 4600, marker: true, out var frame));

        // Only the second access unit surfaces — the half frame was discarded.
        var nals = AnnexBParser.ParseNalUnits(frame!);
        Assert.Single(nals);
        Assert.Equal(0x65, nals[0].Span[0]);
    }

    // ── VP8: packetiser and round trip ──────────────────────────────────────────

    [Fact]
    public void Vp8_descriptor_marks_only_the_first_packet_as_partition_start()
    {
        var payloads = new Vp8Packetiser().Packetise(Frame(3000), maxPayloadSize: 1200);

        Assert.True(payloads.Count >= 3);
        Assert.Equal(0x10, payloads[0].Payload.Span[0]);
        Assert.All(payloads.Skip(1), p => Assert.Equal(0x00, p.Payload.Span[0]));
        Assert.True(payloads[^1].IsLastOfFrame);
        Assert.Equal(1, payloads.Count(p => p.IsLastOfFrame));
        Assert.All(payloads, p => Assert.True(p.Payload.Length <= 1200));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(5000)]
    public void Vp8_round_trip_reproduces_the_frame(int frameSize)
    {
        var frame = Frame(frameSize);
        var payloads = new Vp8Packetiser().Packetise(frame, maxPayloadSize: 1200);

        var depacketiser = new Vp8Depacketiser();
        byte[]? rebuilt = null;
        for (var i = 0; i < payloads.Count; i++)
        {
            var done = depacketiser.TryProcess(payloads[i].Payload, 9000, payloads[i].IsLastOfFrame, out rebuilt);
            Assert.Equal(i == payloads.Count - 1, done);
        }

        Assert.Equal(frame, rebuilt);
    }

    [Fact]
    public void Vp8_extended_descriptor_from_browser_is_skipped()
    {
        // X=1, S=1 | I=1 with 15-bit picture ID | payload — the libwebrtc default shape.
        byte[] payload = [0x90, 0x80, 0x81, 0x02, 0xAA, 0xBB, 0xCC];

        var depacketiser = new Vp8Depacketiser();
        Assert.True(depacketiser.TryProcess(payload, 9000, marker: true, out var frame));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, frame);
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x80, 0x7F, 0xAA, 0xBB })]             // I: 7-bit picture ID
    [InlineData(new byte[] { 0x90, 0x40, 0x11, 0xAA, 0xBB })]             // L: TL0PICIDX
    [InlineData(new byte[] { 0x90, 0x30, 0x22, 0xAA, 0xBB })]             // T+K: shared byte
    [InlineData(new byte[] { 0x90, 0xF0, 0x81, 0x02, 0x11, 0x22, 0xAA, 0xBB })] // all extensions
    public void Vp8_all_extension_layouts_are_skipped(byte[] payload)
    {
        var depacketiser = new Vp8Depacketiser();
        Assert.True(depacketiser.TryProcess(payload, 9000, marker: true, out var frame));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, frame);
    }

    [Fact]
    public void Vp8_timestamp_change_without_marker_never_merges_frames()
    {
        var first = new Vp8Packetiser().Packetise(Frame(3000), maxPayloadSize: 1200);
        var secondFrame = Frame(80);
        var second = new Vp8Packetiser().Packetise(secondFrame, maxPayloadSize: 1200);

        var depacketiser = new Vp8Depacketiser();
        Assert.False(depacketiser.TryProcess(first[0].Payload, 1000, marker: false, out _));
        Assert.True(depacketiser.TryProcess(second[0].Payload, 4600, marker: true, out var frame));
        Assert.Equal(secondFrame, frame);
    }

    [Fact]
    public void Vp8_continuation_without_frame_start_is_dropped()
    {
        var payloads = new Vp8Packetiser().Packetise(Frame(3000), maxPayloadSize: 1200);

        var depacketiser = new Vp8Depacketiser();
        foreach (var p in payloads.Skip(1))
            Assert.False(depacketiser.TryProcess(p.Payload, 9000, p.IsLastOfFrame, out _));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static byte[] Nal(byte header, int bodyLength)
    {
        var nal = new byte[1 + bodyLength];
        nal[0] = header;
        for (var i = 1; i < nal.Length; i++)
            nal[i] = (byte)(1 + (i % 250)); // never 0x00 — keeps Annex-B parsing unambiguous
        return nal;
    }

    private static byte[] AnnexB(params (byte[] Nal, bool LongStartCode)[] nals)
    {
        var stream = new MemoryStream();
        foreach (var (nal, longStartCode) in nals)
        {
            stream.Write(longStartCode ? [0, 0, 0, 1] : [0, 0, 1]);
            stream.Write(nal);
        }

        return stream.ToArray();
    }

    private static byte[] Frame(int length)
    {
        var frame = new byte[length];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i * 7);
        return frame;
    }
}
