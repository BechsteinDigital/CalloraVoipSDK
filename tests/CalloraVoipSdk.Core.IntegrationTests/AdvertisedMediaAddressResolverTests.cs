using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Regression tests for the advertised media address decision (M1 hotfix): a wildcard or
/// loopback signaling bind must never silently advertise loopback towards a LAN peer.
/// </summary>
public sealed class AdvertisedMediaAddressResolverTests
{
    private static readonly IPAddress LanInterface = IPAddress.Parse("192.168.178.20");
    private static readonly IPEndPoint LanPeer = new(IPAddress.Parse("192.168.178.1"), 5060);

    private static IPAddress Resolve(
        IPEndPoint localSignaling,
        IPEndPoint? remoteSignaling,
        Func<string, int, IPAddress?> probe,
        string remoteUri = "sip:017674717849@fritz.box")
        => AdvertisedMediaAddressResolver.Resolve(
            new StubSession(localSignaling, remoteSignaling, remoteUri),
            probe,
            NullLogger.Instance);

    [Fact]
    public void Concrete_non_loopback_bind_address_is_used_directly()
    {
        var result = Resolve(
            new IPEndPoint(LanInterface, 5060),
            LanPeer,
            probe: (_, _) => throw new InvalidOperationException("probe must not run"));

        Assert.Equal(LanInterface, result);
    }

    [Fact]
    public void Loopback_bind_towards_lan_peer_probes_the_route_instead()
    {
        // Regression: the transport runtime normalizes a 0.0.0.0 bind to 127.0.0.1, which
        // used to be advertised verbatim — the peer terminated the call right after answer.
        var result = Resolve(
            new IPEndPoint(IPAddress.Loopback, 43717),
            LanPeer,
            probe: (host, port) =>
            {
                Assert.Equal(LanPeer.Address.ToString(), host);
                Assert.Equal(LanPeer.Port, port);
                return LanInterface;
            });

        Assert.Equal(LanInterface, result);
    }

    [Fact]
    public void Loopback_bind_towards_loopback_peer_stays_loopback()
    {
        var result = Resolve(
            new IPEndPoint(IPAddress.Loopback, 5060),
            new IPEndPoint(IPAddress.Loopback, 5080),
            probe: (_, _) => throw new InvalidOperationException("probe must not run"));

        Assert.Equal(IPAddress.Loopback, result);
    }

    [Fact]
    public void Wildcard_bind_probes_remote_endpoint_before_uri()
    {
        var probedHosts = new List<string>();
        var result = Resolve(
            new IPEndPoint(IPAddress.Any, 5060),
            LanPeer,
            probe: (host, _) =>
            {
                probedHosts.Add(host);
                return LanInterface;
            });

        Assert.Equal(LanInterface, result);
        Assert.Equal([LanPeer.Address.ToString()], probedHosts);
    }

    [Fact]
    public void Unroutable_remote_falls_back_to_uri_probe_then_loopback()
    {
        var probedHosts = new List<string>();
        var result = Resolve(
            new IPEndPoint(IPAddress.Any, 5060),
            LanPeer,
            probe: (host, _) =>
            {
                probedHosts.Add(host);
                return null;
            });

        Assert.Equal(IPAddress.Loopback, result);
        Assert.Equal([LanPeer.Address.ToString(), "fritz.box"], probedHosts);
    }

    [Fact]
    public void Missing_remote_endpoint_uses_uri_probe()
    {
        var result = Resolve(
            new IPEndPoint(IPAddress.Any, 5060),
            remoteSignaling: null,
            probe: (host, port) =>
            {
                Assert.Equal("fritz.box", host);
                Assert.Equal(5060, port);
                return LanInterface;
            });

        Assert.Equal(LanInterface, result);
    }

    private sealed class StubSession : ISipCallSession
    {
        private readonly IPEndPoint _local;
        private readonly IPEndPoint? _remote;

        public StubSession(IPEndPoint local, IPEndPoint? remote, string remoteUri)
        {
            _local = local;
            _remote = remote;
            RemoteUri = remoteUri;
        }

        public string CallId => "resolver-test";
        public string LocalUri => "sip:agent@test.local";
        public string RemoteUri { get; }
        public SipDialogState State => SipDialogState.Established;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => true;
        public string? RemoteAssertedIdentity => null;
        public string? RemoteSdp => null;
        public IPEndPoint LocalSignalingEndPoint => _local;
        public IPEndPoint? RemoteSignalingEndPoint => _remote;

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }
        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived { add { } remove { } }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default) => Task.CompletedTask;
        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, int durationMs = 160, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SendReferAsync(string referTo, string? referredBy = null, bool suppressSubscription = false, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SendOptionsAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default) => Task.FromResult(false);
        public void Dispose() { }
    }
}
