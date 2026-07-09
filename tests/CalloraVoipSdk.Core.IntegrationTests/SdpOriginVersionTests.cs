using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDP origin versioning (package B.5, RFC 4566 §5.2 / RFC 3264 §5): the <c>o=</c> line must
/// carry a stable per-session id and a version that increments on every locally built SDP so a
/// peer detects hold/re-INVITE modifications. Previously it was the constant <c>o=- 0 0</c>.
/// </summary>
public sealed class SdpOriginVersionTests
{
    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Loopback, 40000);

    private static (long Id, long Version) ParseOrigin(string sdp)
    {
        var line = sdp
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .First(l => l.StartsWith("o=", StringComparison.Ordinal));

        // o=- <sess-id> <sess-version> IN IP4 <addr>
        var parts = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (long.Parse(parts[1]), long.Parse(parts[2]));
    }

    private static readonly string PlainOffer =
        "v=0\r\no=- 111 222 IN IP4 203.0.113.7\r\ns=peer\r\nc=IN IP4 203.0.113.7\r\nt=0 0\r\n"
        + "m=audio 20000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n";

    // ── SdpUtilities plumbing: options -> o= line ──────────────────────────────

    [Fact]
    public void Built_offer_carries_the_requested_session_id_and_version()
    {
        var options = new SdpMediaNegotiationOptions { SessionId = 987654, SessionVersion = 42 };

        var offer = SdpUtilities.BuildDefaultSdp(LocalEndPoint, hold: false, options);

        var (id, version) = ParseOrigin(offer);
        Assert.Equal(987654, id);
        Assert.Equal(42, version);
    }

    [Fact]
    public void Negotiated_answer_carries_the_requested_session_id_and_version()
    {
        var options = new SdpMediaNegotiationOptions { SessionId = 555, SessionVersion = 7 };

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(PlainOffer, LocalEndPoint, hold: false, options);

        Assert.NotNull(answer);
        var (id, version) = ParseOrigin(answer!);
        Assert.Equal(555, id);
        Assert.Equal(7, version);
    }

    [Fact]
    public void Without_session_options_the_origin_stays_the_legacy_constant()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalEndPoint, hold: false, options: null);

        Assert.Equal((0, 0), ParseOrigin(offer));
    }

    // ── Channel behavior: version increments, id stable (RFC 3264 §5) ───────────

    [Fact]
    public async Task Successive_local_sdps_keep_the_session_id_and_increment_the_version()
    {
        using var channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Optional,
            policySource: "test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (id1, v1) = ParseOrigin(await channel.BuildOfferSdpAsync(LocalEndPoint, hold: false, cts.Token));
        var (id2, v2) = ParseOrigin(await channel.BuildOfferSdpAsync(LocalEndPoint, hold: false, cts.Token));

        Assert.Equal(id1, id2);                    // stable session id across the leg
        Assert.True(v2 > v1, $"expected version to increment: {v1} -> {v2}");
    }

    [Fact]
    public void Distinct_channels_use_distinct_session_ids()
    {
        SipCoreCallChannel Create() => new(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Optional,
            "test");

        using var a = Create();
        using var b = Create();

        var idA = ParseOrigin(a.BuildOfferSdpAsync(LocalEndPoint, hold: false, CancellationToken.None).Result).Id;
        var idB = ParseOrigin(b.BuildOfferSdpAsync(LocalEndPoint, hold: false, CancellationToken.None).Result).Id;

        Assert.NotEqual(idA, idB);
    }
}
