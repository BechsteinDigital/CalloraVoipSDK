using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L0 — #223: the opaque video payload format. Once a browser encrypts its frames before the packetiser
/// (WebRTC Encoded Transform / SFrame, RFC 9605), the frame is ciphertext: the H.264 pair fails closed on it
/// (NAL-type dispatch, Annex-B parsing) and the VP8 depacketiser reads its key-frame bit out of the encrypted
/// first frame byte, producing a random flag. These tests pin that the opaque pair carries arbitrary content
/// through untouched, claims nothing about it, and that the non-opaque pair is unchanged.
/// </summary>
public sealed class OpaqueVideoPayloadFormatTests
{
    private const int MaxPayloadSize = 1200;

    /// <summary>Acceptance criterion: a frame of random content survives packetise → depacketise byte-identically.</summary>
    [Theory]
    [InlineData("VP8")]
    [InlineData("H264")]
    public void A_random_frame_round_trips_byte_identically(string codec)
    {
        var frame = RandomFrame(200_000, seed: 4711);
        var (packetiser, depacketiser) = VideoPayloadFormat.CreateOpaque(codec);

        var received = RoundTrip(packetiser, depacketiser, frame, out var isKeyFrame);

        Assert.Equal(frame, received);
        // No key-frame claim is made about content the SDK did not read; that signal belongs in a plaintext
        // header extension (Dependency Descriptor) — the follow-up work #223 points at.
        Assert.False(isKeyFrame);
    }

    /// <summary>
    /// Content that happens to look like codec syntax must change nothing: a VP8 frame whose first byte has the
    /// P bit clear (a key frame in the clear-text format) and an H.264 frame starting with an IDR NAL header
    /// still round-trip verbatim and are still reported as "not a key frame".
    /// </summary>
    [Theory]
    [InlineData("VP8", (byte)0x00)]   // P=0 → the non-opaque path would call this a key frame
    [InlineData("H264", (byte)0x65)]  // NAL type 5 = IDR → likewise
    public void Content_that_looks_like_codec_syntax_is_not_interpreted(string codec, byte firstByte)
    {
        var frame = RandomFrame(5_000, seed: 99);
        frame[0] = firstByte;
        var (packetiser, depacketiser) = VideoPayloadFormat.CreateOpaque(codec);

        var received = RoundTrip(packetiser, depacketiser, frame, out var isKeyFrame);

        Assert.Equal(frame, received);
        Assert.False(isKeyFrame);
    }

    /// <summary>
    /// The reason the opaque H.264 packetiser fragments every frame: a single-NAL packet would place the frame's
    /// first byte where a receiver reads the NAL type, so ciphertext starting with an FU-A (0x1C) or STAP-A
    /// (0x18) type would be mistaken for framing. With FU-A throughout, both survive.
    /// </summary>
    [Theory]
    [InlineData((byte)0x1C)]
    [InlineData((byte)0x18)]
    public void Opaque_h264_survives_content_that_emulates_a_framing_header(byte firstByte)
    {
        var frame = RandomFrame(64, seed: 7);
        frame[0] = firstByte;
        var (packetiser, depacketiser) = VideoPayloadFormat.CreateOpaque("H264");

        var received = RoundTrip(packetiser, depacketiser, frame, out _);

        Assert.Equal(frame, received);
    }

    [Theory]
    [InlineData("VP8")]
    [InlineData("H264")]
    public void Consecutive_frames_are_each_delivered_once(string codec)
    {
        var first = RandomFrame(3_000, seed: 1);
        var second = RandomFrame(3_000, seed: 2);
        var (packetiser, depacketiser) = VideoPayloadFormat.CreateOpaque(codec);

        var receivedFirst = RoundTrip(packetiser, depacketiser, first, out _, rtpTimestamp: 1000);
        var receivedSecond = RoundTrip(packetiser, depacketiser, second, out _, rtpTimestamp: 4000);

        Assert.Equal(first, receivedFirst);
        Assert.Equal(second, receivedSecond);
        Assert.Equal(0, depacketiser.DiscardedPacketCount);
    }

    /// <summary>
    /// The opaque H.264 depacketiser reads only its counterpart's framing. A single-NAL or STAP-A packet would
    /// require treating the leading content byte as a header, so it is refused and counted rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData((byte)0x41)]  // single NAL unit (type 1)
    [InlineData((byte)0x18)]  // STAP-A
    [InlineData((byte)0x1D)]  // FU-B — not the framing this pair writes
    public void Opaque_h264_refuses_packetisation_modes_it_did_not_write(byte nalHeader)
    {
        var depacketiser = new OpaqueH264Depacketiser();
        var payload = new byte[] { nalHeader, 0x11, 0x22, 0x33 };

        Assert.False(depacketiser.TryProcess(payload, rtpTimestamp: 1, marker: true, out var frame, out _));
        Assert.Null(frame);
        Assert.Equal(1, depacketiser.DiscardedPacketCount);
    }

    /// <summary>K4: a fragment run that never ends must be bounded, then discarded — never grow without limit.</summary>
    [Fact]
    public void Opaque_h264_bounds_a_never_terminated_fragment_run()
    {
        var depacketiser = new OpaqueH264Depacketiser(maxFrameBytes: 4_096);
        var (packetiser, _) = VideoPayloadFormat.CreateOpaque("H264");
        var payloads = packetiser.Packetise(RandomFrame(20_000, seed: 3), MaxPayloadSize);

        var delivered = false;
        foreach (var payload in payloads)
            delivered |= depacketiser.TryProcess(payload.Payload, rtpTimestamp: 1, marker: false, out _, out _);

        Assert.False(delivered);
        Assert.Equal(1, depacketiser.OversizedFrameDiscardCount);
    }

    /// <summary>K4, VP8 side: the same bound on a markerless same-timestamp run.</summary>
    [Fact]
    public void Opaque_vp8_bounds_a_markerless_run()
    {
        var depacketiser = new OpaqueVp8Depacketiser(maxFrameBytes: 4_096);
        var (packetiser, _) = VideoPayloadFormat.CreateOpaque("VP8");
        var payloads = packetiser.Packetise(RandomFrame(20_000, seed: 3), MaxPayloadSize);

        var delivered = false;
        foreach (var payload in payloads)
            delivered |= depacketiser.TryProcess(payload.Payload, rtpTimestamp: 1, marker: false, out _, out _);

        Assert.False(delivered);
        Assert.Equal(1, depacketiser.OversizedFrameDiscardCount);
    }

    /// <summary>
    /// Acceptance criterion: the non-opaque paths are untouched. Both still derive the key-frame flag from the
    /// frame, which is exactly what makes them unsuitable for encrypted media and correct for clear media.
    /// </summary>
    [Fact]
    public void The_non_opaque_pair_still_detects_key_frames()
    {
        var (vp8Packetiser, vp8Depacketiser) = VideoPayloadFormat.Create("VP8");
        var vp8Frame = new byte[] { 0x00, 0x01, 0x02, 0x03 }; // P=0 → key frame (RFC 7741 §4.3)
        RoundTrip(vp8Packetiser, vp8Depacketiser, vp8Frame, out var vp8IsKeyFrame);
        Assert.True(vp8IsKeyFrame);

        var (h264Packetiser, h264Depacketiser) = VideoPayloadFormat.Create("H264");
        // Annex-B access unit with a single IDR NAL (type 5).
        var h264Frame = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0xAA, 0xBB };
        RoundTrip(h264Packetiser, h264Depacketiser, h264Frame, out var h264IsKeyFrame);
        Assert.True(h264IsKeyFrame);
    }

    /// <summary>An unknown codec has no opaque pair either — fail loudly instead of silently degrading.</summary>
    [Fact]
    public void An_unsupported_codec_has_no_opaque_pair()
        => Assert.Throws<InvalidOperationException>(() => VideoPayloadFormat.CreateOpaque("AV1"));

    // Feeds every payload of one frame through the depacketiser in order, marker on the last, and returns the
    // reassembled frame.
    private static byte[] RoundTrip(
        IVideoPacketiser packetiser,
        IVideoDepacketiser depacketiser,
        byte[] frame,
        out bool isKeyFrame,
        uint rtpTimestamp = 1000)
    {
        var payloads = packetiser.Packetise(frame, MaxPayloadSize);
        byte[]? received = null;
        isKeyFrame = false;

        foreach (var payload in payloads)
        {
            if (depacketiser.TryProcess(payload.Payload, rtpTimestamp, payload.IsLastOfFrame, out var completed, out var key))
            {
                received = completed;
                isKeyFrame = key;
            }
        }

        Assert.NotNull(received);
        return received!;
    }

    private static byte[] RandomFrame(int length, int seed)
    {
        var frame = new byte[length];
        new Random(seed).NextBytes(frame);
        return frame;
    }
}
