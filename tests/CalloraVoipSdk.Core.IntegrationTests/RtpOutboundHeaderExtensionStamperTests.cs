using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound RTP header-extension stamping (ADR-010 B2c): the transport-wide counter (transport-cc) and,
/// on a BUNDLE transport, the MID SDES token. Pins that the non-BUNDLE output is byte-identical to
/// stamping transport-cc alone (so the existing send path is unchanged) and that MID coexists with it.
/// </summary>
public sealed class RtpOutboundHeaderExtensionStamperTests
{
    [Fact]
    public void Transport_cc_only_is_byte_identical_to_the_direct_encoding()
    {
        var stamper = new RtpOutboundHeaderExtensionStamper(
            transportWideCcExtensionId: 5, midExtensionId: null, mid: null);

        var built = stamper.Build(transportCcSequence: 12_345);
        var direct = OneByteRtpHeaderExtensions.EncodeTransportSequenceNumber(5, 12_345);

        Assert.NotNull(built);
        Assert.Equal(direct.Profile, built!.Profile);
        Assert.Equal(direct.Data.ToArray(), built.Data.ToArray());
    }

    [Fact]
    public void No_configured_extensions_stamp_nothing()
    {
        var stamper = new RtpOutboundHeaderExtensionStamper(null, null, null);

        Assert.False(stamper.StampsAnything);
        Assert.Null(stamper.Build(transportCcSequence: 7));
        Assert.Null(stamper.Build(transportCcSequence: null));
    }

    [Fact]
    public void Mid_only_stamps_the_mid()
    {
        var stamper = new RtpOutboundHeaderExtensionStamper(
            transportWideCcExtensionId: null, midExtensionId: 3, mid: "audio");

        var ext = stamper.Build(transportCcSequence: 999); // no transport-cc id → the counter is ignored
        Assert.True(RtpMidHeaderExtension.TryRead(ext, id: 3, out var mid));
        Assert.Equal("audio", mid);
        Assert.False(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(ext, id: 5, out _));
    }

    [Fact]
    public void Mid_and_transport_cc_are_both_stamped()
    {
        var stamper = new RtpOutboundHeaderExtensionStamper(
            transportWideCcExtensionId: 5, midExtensionId: 3, mid: "video");

        var ext = stamper.Build(transportCcSequence: 40_000);

        Assert.True(RtpMidHeaderExtension.TryRead(ext, id: 3, out var mid));
        Assert.Equal("video", mid);
        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(ext, id: 5, out var seq));
        Assert.Equal(40_000, seq);
    }

    [Fact]
    public void Mid_only_reuses_the_constant_extension_across_packets()
    {
        var stamper = new RtpOutboundHeaderExtensionStamper(null, midExtensionId: 3, mid: "audio");

        // MID is constant and there is no per-packet counter → the same extension object is reused.
        Assert.Same(stamper.Build(null), stamper.Build(null));
    }

    [Fact]
    public void Constructor_validates_the_mid_length()
    {
        Assert.Throws<ArgumentException>(
            () => new RtpOutboundHeaderExtensionStamper(null, midExtensionId: 3, mid: "0123456789abcdefg")); // 17 bytes
    }
}
