using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Builds the RFC 8843 §9.2 demultiplexer from the negotiated m-lines (ADR-010 B2c/B4): each unambiguous
/// payload type maps to its MID, payload types shared across m-lines are dropped from the PT fallback and
/// must be resolved by MID/SSRC instead.
/// </summary>
public sealed class BundledRtpDemultiplexerFactoryTests
{
    private const byte MidExtId = 3;

    private static RtpPacket Packet(uint ssrc, int pt, string? mid = null) => new()
    {
        Ssrc = ssrc,
        PayloadType = (byte)pt,
        HeaderExtension = mid is null ? null : RtpMidHeaderExtension.Encode(MidExtId, mid),
    };

    [Fact]
    public void Unambiguous_payload_types_route_without_a_mid()
    {
        var demux = BundledRtpDemultiplexerFactory.Create(MidExtId, new Dictionary<string, IReadOnlyCollection<int>>
        {
            ["audio"] = new[] { 0, 8, 111 },
            ["video"] = new[] { 96, 97 },
        });

        Assert.True(demux.TryResolveMid(Packet(10, 111), out var a)); // no MID → PT fallback
        Assert.Equal("audio", a);
        Assert.True(demux.TryResolveMid(Packet(20, 96), out var v));
        Assert.Equal("video", v);
    }

    [Fact]
    public void A_payload_type_shared_across_m_lines_is_not_used_for_the_fallback()
    {
        // Both m-lines negotiated PT 96 → it cannot disambiguate; only MID/SSRC can.
        var demux = BundledRtpDemultiplexerFactory.Create(MidExtId, new Dictionary<string, IReadOnlyCollection<int>>
        {
            ["audio"] = new[] { 96, 111 },
            ["video"] = new[] { 96, 97 },
        });

        Assert.False(demux.TryResolveMid(Packet(10, 96), out _)); // shared PT, no MID → undemuxable
        Assert.True(demux.TryResolveMid(Packet(10, 96, mid: "video"), out var v)); // MID resolves it
        Assert.Equal("video", v);
        Assert.True(demux.TryResolveMid(Packet(20, 111), out var a)); // 111 is unique → still routes
        Assert.Equal("audio", a);
    }

    [Fact]
    public void Known_mids_come_from_the_negotiated_m_lines()
    {
        var demux = BundledRtpDemultiplexerFactory.Create(MidExtId, new Dictionary<string, IReadOnlyCollection<int>>
        {
            ["audio"] = new[] { 0 },
            ["video"] = new[] { 96 },
        });

        Assert.True(demux.TryResolveMid(Packet(1, 0, mid: "audio"), out _));
        Assert.False(demux.TryResolveMid(Packet(2, 0, mid: "screenshare"), out _)); // unknown MID dropped
    }

    [Fact]
    public void An_empty_mid_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => BundledRtpDemultiplexerFactory.Create(
            MidExtId, new Dictionary<string, IReadOnlyCollection<int>> { [""] = new[] { 0 } }));
    }
}
