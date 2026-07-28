using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// The outbound-codec adaptation policy (issue #18, A2). A symmetric UAC may follow the peer onto a
/// different negotiated codec, but must never start sending a payload type the peer did not
/// negotiate for the leg (RFC 3264 §5.1). Both platform devices route send-adaptation through this
/// single policy, so its behaviour defines theirs.
/// </summary>
public sealed class OutboundCodecAdaptationPolicyTests
{
    private static readonly int[] NegotiatedMap = { 0, 8, 96 };

    [Fact]
    public void Adapts_to_a_negotiated_payload_type_that_differs_from_the_current_one()
    {
        var decision = OutboundCodecAdaptationPolicy.Evaluate(
            inboundPayloadType: 8,
            currentOutboundPayloadType: 0,
            negotiatedPayloadType: 0,
            negotiatedPayloadTypes: NegotiatedMap);

        Assert.True(decision.ShouldAdapt);
        Assert.Equal(8, decision.TargetPayloadType);
    }

    [Fact]
    public void Does_not_adapt_to_an_unnegotiated_payload_type_even_if_it_is_a_static_codec()
    {
        // PT 9 (G.722) is a well-known static type, but it was never negotiated for this leg —
        // echoing it back would send a codec the peer never agreed to receive.
        var decision = OutboundCodecAdaptationPolicy.Evaluate(
            inboundPayloadType: 9,
            currentOutboundPayloadType: 0,
            negotiatedPayloadType: 0,
            negotiatedPayloadTypes: NegotiatedMap);

        Assert.False(decision.ShouldAdapt);
        Assert.Equal(OutboundCodecAdaptationDecision.NoChange, decision);
    }

    [Fact]
    public void Adapts_to_the_primary_negotiated_payload_type_even_when_absent_from_the_map()
    {
        // The primary negotiated PT need not be repeated in the map keys.
        var decision = OutboundCodecAdaptationPolicy.Evaluate(
            inboundPayloadType: 18,
            currentOutboundPayloadType: 0,
            negotiatedPayloadType: 18,
            negotiatedPayloadTypes: Array.Empty<int>());

        Assert.True(decision.ShouldAdapt);
        Assert.Equal(18, decision.TargetPayloadType);
    }

    [Fact]
    public void Does_not_adapt_when_the_inbound_type_already_matches_the_current_outbound_type()
    {
        var decision = OutboundCodecAdaptationPolicy.Evaluate(
            inboundPayloadType: 8,
            currentOutboundPayloadType: 8,
            negotiatedPayloadType: 0,
            negotiatedPayloadTypes: NegotiatedMap);

        Assert.False(decision.ShouldAdapt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(128)]
    public void Rejects_payload_types_outside_the_valid_7_bit_range(int inbound)
    {
        var decision = OutboundCodecAdaptationPolicy.Evaluate(
            inboundPayloadType: inbound,
            currentOutboundPayloadType: 0,
            negotiatedPayloadType: 0,
            negotiatedPayloadTypes: NegotiatedMap);

        Assert.False(decision.ShouldAdapt);
    }

    [Fact]
    public void Null_negotiated_set_throws()
    {
        Assert.Throws<ArgumentNullException>(() => OutboundCodecAdaptationPolicy.Evaluate(
            8, 0, 0, negotiatedPayloadTypes: null!));
    }
}
