using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The public value objects reject what they cannot represent (#165 P3-12). A <c>CallId</c> built from the
/// empty GUID is not an identifier — it keys the call registry, the media orchestrator's active map and the
/// per-call SSRC bookkeeping, and every empty one collides with every other. An ICE candidate's fields are
/// inputs to pairing and prioritisation, not descriptions, so an unusable value costs a call that never
/// connects with nothing pointing at the candidate behind it. The bounds match what the SDP parser already
/// enforces on the wire, so a candidate that came off an offer passes them by construction.
/// </summary>
public sealed class DomainValueObjectValidationTests
{
    private static CallIceCandidate Candidate(
        string foundation = "1",
        int component = 1,
        string transport = "udp",
        long priority = 100,
        string address = "127.0.0.1",
        int port = 5000,
        string type = "host",
        int? relatedPort = null) => new()
    {
        Foundation = foundation,
        Component = component,
        Transport = transport,
        Priority = priority,
        Address = address,
        Port = port,
        Type = type,
        RelatedPort = relatedPort,
    };

    [Fact]
    public void An_empty_call_id_is_rejected_while_a_real_one_round_trips()
    {
        Assert.Throws<ArgumentException>(() => new CallId(Guid.Empty));

        var guid = Guid.NewGuid();
        var id = new CallId(guid);

        Assert.Equal(guid, id.Value);
        Assert.NotEqual(CallId.New(), id);
    }

    [Fact]
    public void A_with_clone_of_a_call_id_is_validated_too()
    {
        var id = CallId.New();

        Assert.Throws<ArgumentException>(() => id with { Value = Guid.Empty });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_candidate_foundation_is_rejected(string foundation)
        => Assert.Throws<ArgumentException>(() => Candidate(foundation: foundation));

    [Fact]
    public void A_foundation_longer_than_the_grammar_allows_is_rejected()
        => Assert.Throws<ArgumentException>(() => Candidate(foundation: new string('a', 33)));

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void A_component_outside_the_valid_range_is_rejected(int component)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Candidate(component: component));

    [Theory]
    [InlineData(0L)]
    [InlineData((long)int.MaxValue + 1)]
    public void A_priority_outside_the_valid_range_is_rejected(long priority)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Candidate(priority: priority));

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void A_port_outside_the_valid_range_is_rejected(int port)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Candidate(port: port));

    [Fact]
    public void Port_zero_stays_valid_because_it_marks_a_disabled_candidate()
        => Assert.Equal(0, Candidate(port: 0).Port);

    [Fact]
    public void A_blank_transport_or_address_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Candidate(transport: " "));
        Assert.Throws<ArgumentException>(() => Candidate(address: ""));
    }

    [Fact]
    public void An_unknown_candidate_type_is_rejected_and_the_known_ones_pass()
    {
        Assert.Throws<ArgumentException>(() => Candidate(type: "wormhole"));

        foreach (var type in new[] { "host", "srflx", "prflx", "relay", "HOST" })
            Assert.Equal(type, Candidate(type: type).Type);
    }

    [Fact]
    public void A_related_port_is_bounded_when_set_and_optional_otherwise()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Candidate(relatedPort: 70000));
        Assert.Null(Candidate().RelatedPort);
        Assert.Equal(5000, Candidate(relatedPort: 5000).RelatedPort);
    }
}
