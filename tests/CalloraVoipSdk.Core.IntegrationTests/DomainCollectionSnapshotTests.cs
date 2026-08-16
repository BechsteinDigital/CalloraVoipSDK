using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The domain's collection properties take a snapshot of what the caller passes (#165 P3-10). An
/// <c>IReadOnly*</c> parameter is a view, not a guarantee: storing the reference verbatim leaves the caller's
/// own mutable collection live behind an object the SDK treats as immutable and reads long after it was
/// handed over — the enricher chain clones <see cref="CallMediaParameters"/> with <c>with</c> (K2), dial
/// options are read when the INVITE is built, and an account is read on every inbound INVITE for the life of
/// its line.
/// </summary>
public sealed class DomainCollectionSnapshotTests
{
    private static CallIceCandidate Candidate() => new()
    {
        Foundation = "1",
        Component = 1,
        Transport = "udp",
        Priority = 100,
        Address = "127.0.0.1",
        Port = 5000,
        Type = "host",
    };

    private static CallMediaParameters Parameters(
        IReadOnlyList<CallIceCandidate>? local = null,
        IReadOnlyList<CallIceCandidate>? remote = null,
        IReadOnlyDictionary<int, string>? codecs = null) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 6000),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 6002),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        LocalIceCandidates = local ?? [],
        RemoteIceCandidates = remote ?? [],
        PayloadTypeCodecMap = codecs ?? new Dictionary<int, string>(),
    };

    [Fact]
    public void Call_media_parameters_do_not_track_the_callers_collections()
    {
        var candidates = new List<CallIceCandidate>
        {
            Candidate(),
        };
        var codecs = new Dictionary<int, string> { [0] = "PCMU" };

        var parameters = Parameters(local: candidates, codecs: codecs);

        candidates.Clear();
        codecs[0] = "PCMA";
        codecs[8] = "PCMA";

        Assert.Single(parameters.LocalIceCandidates);
        Assert.Equal("PCMU", parameters.PayloadTypeCodecMap[0]);
        Assert.Single(parameters.PayloadTypeCodecMap);
    }

    [Fact]
    public void A_with_clone_keeps_the_snapshot_and_does_not_re_copy_it()
    {
        var parameters = Parameters(remote: [Candidate()]);

        var enriched = parameters with { PayloadType = 8 };

        // The enricher chain clones these on every step (K2); re-snapshotting per clone would be pure waste.
        Assert.Same(parameters.RemoteIceCandidates, enriched.RemoteIceCandidates);
    }

    [Fact]
    public void Dial_options_do_not_track_the_callers_header_dictionary()
    {
        var headers = new Dictionary<string, string> { ["X-Tenant"] = "acme" };
        var options = new DialOptions { CustomHeaders = headers };

        headers["X-Tenant"] = "evil";
        headers["X-Extra"] = "injected";

        Assert.Equal("acme", options.CustomHeaders!["X-Tenant"]);
        Assert.Single(options.CustomHeaders);
    }

    [Fact]
    public void A_sip_account_does_not_track_the_callers_inbound_numbers()
    {
        var numbers = new List<string> { "+4930111" };
        var account = new SipAccount { Username = "u", SipServer = "s", InboundNumbers = numbers };

        numbers.Add("+4930222");

        Assert.Single(account.InboundNumbers!);
    }

    [Fact]
    public void Null_collections_stay_null_and_empty_ones_stay_empty()
    {
        var options = new DialOptions();
        var account = new SipAccount { Username = "u", SipServer = "s" };
        var parameters = Parameters();

        Assert.Null(options.CustomHeaders);
        Assert.Null(account.InboundNumbers);
        Assert.Empty(parameters.LocalIceCandidates);
        Assert.Empty(parameters.PayloadTypeCodecMap);
    }
}
