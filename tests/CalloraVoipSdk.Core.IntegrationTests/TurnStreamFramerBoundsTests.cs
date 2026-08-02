using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Bounds gate for <see cref="TurnStreamFramer"/> (HARD-A7). A STUN control frame whose declared
/// body exceeds the frame ceiling must be refused before the frame buffer is allocated, while a
/// valid small STUN frame and a large ChannelData frame (a legitimately relayed peer datagram)
/// still parse.
/// </summary>
public sealed class TurnStreamFramerBoundsTests
{
    private static MemoryStream Stream(byte[] bytes) => new(bytes, writable: false);

    // #155 P2-1: over a stream, ChannelData is padded to a 4-byte boundary (RFC 8656 §12.5) with 0-3 bytes not
    // counted in the length field. The framer must consume that padding, or the next frame starts mid-stream and
    // the relay desyncs. Covered for every payload length residue class (0-3 mod 4).
    [Theory]
    [InlineData(0)]  // payload 0 mod 4 → 0 pad
    [InlineData(1)]  // 1 mod 4 → 3 pad
    [InlineData(2)]  // 2 mod 4 → 2 pad
    [InlineData(3)]  // 3 mod 4 → 1 pad
    public async Task Padded_channel_data_keeps_the_next_stream_frame_aligned(int payloadLen)
    {
        var payload = new byte[payloadLen];
        for (var i = 0; i < payloadLen; i++)
            payload[i] = (byte)(i + 1);

        var first = TurnChannelDataCodec.Encode(0x4001, payload, padToFourBytes: true);
        var second = TurnChannelDataCodec.Encode(0x4002, [0xAA, 0xBB, 0xCC], padToFourBytes: true);
        Assert.Equal(0, first.Length % 4);    // each frame is 4-byte aligned on the wire
        Assert.Equal(0, second.Length % 4);

        var wire = new byte[first.Length + second.Length];
        first.CopyTo(wire, 0);
        second.CopyTo(wire, first.Length);
        using var stream = Stream(wire);

        var f1 = await TurnStreamFramer.ReadFrameAsync(stream);
        Assert.True(f1!.IsChannelData);
        Assert.Equal(0x4001, f1.ChannelNumber);
        Assert.Equal(payload, f1.Payload);    // the parsed payload excludes the padding

        // The second frame parses correctly ONLY if the first frame's padding was consumed.
        var f2 = await TurnStreamFramer.ReadFrameAsync(stream);
        Assert.True(f2!.IsChannelData);
        Assert.Equal(0x4002, f2.ChannelNumber);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, f2.Payload);
    }

    [Fact]
    public async Task ReadFrame_rejects_oversized_stun_frame_before_allocating()
    {
        // STUN framing (first two bytes below 0x4000) with a declared body of 20000 > 16384. Only the
        // 4-byte header is supplied: without the guard the framer would allocate ~20 KiB and then fail
        // on EOF; the guard must reject on the declared length first.
        var header = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), 0x0001); // Binding method
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 20000);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => TurnStreamFramer.ReadFrameAsync(Stream(header)));
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFrame_parses_valid_small_stun_frame()
    {
        // Body length 8 → total STUN message 28 bytes (type+length already in the 4-byte header).
        var frame = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 8);

        var result = await TurnStreamFramer.ReadFrameAsync(Stream(frame));

        Assert.NotNull(result);
        Assert.False(result!.IsChannelData);
        Assert.Equal(28, result.Payload.Length);
    }

    [Fact]
    public async Task ReadFrame_allows_large_channel_data_frame()
    {
        // ChannelData (first two bytes in 0x4000-0x7FFF) carries a relayed datagram and is deliberately
        // not capped below the 16-bit maximum — a 20000-byte channel payload must still parse.
        const int channelLength = 20000;
        var frame = new byte[4 + channelLength];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), 0x4001); // channel number
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), channelLength);

        var result = await TurnStreamFramer.ReadFrameAsync(Stream(frame));

        Assert.NotNull(result);
        Assert.True(result!.IsChannelData);
        Assert.Equal(0x4001, result.ChannelNumber);
        Assert.Equal(channelLength, result.Payload.Length);
    }
}
