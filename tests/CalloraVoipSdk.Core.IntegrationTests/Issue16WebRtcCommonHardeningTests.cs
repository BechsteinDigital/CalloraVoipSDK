using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #16 media/network-hardening fixes: the WebRTC media socket must follow the configured
/// address family (IPv6 bind must not throw), DNS resolution with an empty result must fail with a
/// host-qualified message rather than the generic "Sequence contains no elements", and cancelling a
/// non-head scheduler entry must not wake the worker for a wasted re-evaluation.
/// </summary>
public sealed class Issue16WebRtcCommonHardeningTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    // --- WebRTC 1: media socket honours the configured address family ---------------------------

    [Fact]
    public async Task An_ipv6_local_endpoint_binds_the_media_socket_as_ipv6()
    {
        // Regression guard: the media socket was hardcoded to AddressFamily.InterNetwork, so an IPv6
        // LocalEndPoint threw on bind (family mismatch). CreateOffer forces the early media bind.
        await using var peer = PeerAt(new IPEndPoint(IPAddress.IPv6Loopback, 0));

        peer.CreateOffer(); // binds the media socket; would throw a SocketException before the fix

        Assert.NotNull(peer.LocalMediaEndPoint);
        Assert.Equal(AddressFamily.InterNetworkV6, peer.LocalMediaEndPoint!.AddressFamily);
    }

    [Fact]
    public async Task An_ipv4_local_endpoint_still_binds_the_media_socket_as_ipv4()
    {
        await using var peer = PeerAt(new IPEndPoint(IPAddress.Loopback, 0));

        peer.CreateOffer();

        Assert.NotNull(peer.LocalMediaEndPoint);
        Assert.Equal(AddressFamily.InterNetwork, peer.LocalMediaEndPoint!.AddressFamily);
    }

    // --- WebRTC 2: empty DNS resolution yields a host-qualified error ---------------------------

    [Fact]
    public void An_empty_resolution_throws_a_host_qualified_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RemoteEndPointResolver.SelectEndPoint("pbx.example.test", Array.Empty<IPAddress>(), 5060));

        Assert.Contains("pbx.example.test", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Sequence contains no elements", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolution_prefers_ipv4_and_falls_back_to_the_first_address()
    {
        var ipv4First = RemoteEndPointResolver.SelectEndPoint(
            "host",
            [IPAddress.Parse("2001:db8::1"), IPAddress.Parse("203.0.113.9")],
            5060);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 5060), ipv4First);

        var ipv6Only = RemoteEndPointResolver.SelectEndPoint(
            "host",
            [IPAddress.Parse("2001:db8::1")],
            5060);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 5060), ipv6Only);
    }

    // --- Scheduler 5a: cancelling a non-head entry does not wake the worker ----------------------

    [Fact]
    public void Cancelling_a_non_head_entry_still_fires_the_earlier_head_entry()
    {
        using var scheduler = new ScheduledActionScheduler(NullLogger.Instance);
        var headFired = new ManualResetEventSlim(false);

        // The head (soonest) entry must still fire even though a later entry is cancelled: the
        // optimisation must never strand a still-due callback.
        var head = scheduler.Schedule(TimeSpan.FromMilliseconds(80), () => headFired.Set());
        using var later = scheduler.Schedule(TimeSpan.FromSeconds(30), () => { });

        later.Dispose(); // cancels a non-head entry — must not disturb the head's due time

        Assert.True(headFired.Wait(TimeSpan.FromSeconds(5)), "the earlier head entry must still fire");
        head.Dispose();
    }

    [Fact]
    public void Cancelling_the_head_entry_lets_the_next_entry_fire_on_time()
    {
        using var scheduler = new ScheduledActionScheduler(NullLogger.Instance);
        var secondFired = new ManualResetEventSlim(false);

        var head = scheduler.Schedule(TimeSpan.FromSeconds(30), () => { });
        using var second = scheduler.Schedule(TimeSpan.FromMilliseconds(80), () => secondFired.Set());

        head.Dispose(); // cancels the current head; the worker must re-evaluate and pick the second

        Assert.True(secondFired.Wait(TimeSpan.FromSeconds(5)), "the next entry must fire after a head cancellation");
        second.Dispose();
    }

    private static WebRtcPeerConnection PeerAt(IPEndPoint localEndPoint) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = localEndPoint,
                AudioCodecs = Pcmu,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), DtlsCertificate.GenerateEcdsaP256(),
            NullLoggerFactory.Instance);
}
