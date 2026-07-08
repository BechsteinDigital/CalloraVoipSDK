using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Package N2 flow (AK4): a registrar reflecting a NAT-public address triggers exactly
/// one corrective re-registration — never a third REGISTER — because the second 200 OK
/// reflects the now-adopted address and produces no state change.
/// </summary>
public sealed class SipRportContactFlowTests
{
    [Fact]
    public async Task Reflected_public_address_triggers_exactly_one_corrective_register()
    {
        var registration = new RecordingRegistrationService("83.135.5.138", 6543);
        var channel = new SipLineChannel(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            "test/1.0",
            registration,
            new NoopSignalingService(),
            new NoopSdpNegotiator(),
            iceAgent: null,
            SrtpPolicy.Optional,
            telemetry: null,
            NullLoggerFactory.Instance);

        channel.StartRegistration(_ => { });

        // First REGISTER advertises local, learns the public address, re-registers once.
        // The second 200 OK reflects the same address → no change → refresh (not a 3rd).
        await PollUntil(() => registration.Requests.Count >= 2);
        await Task.Delay(150);

        Assert.Equal(2, registration.Requests.Count);
        Assert.Null(registration.Requests[0].PublicHost);
        Assert.Equal("83.135.5.138", registration.Requests[1].PublicHost);
        Assert.Equal(6543, registration.Requests[1].PublicPort);

        channel.Dispose();
    }

    [Fact]
    public async Task Manual_override_suppresses_rport_correction()
    {
        var registration = new RecordingRegistrationService("83.135.5.138", 6543);
        var channel = new SipLineChannel(
            new SipAccount
            {
                Username = "u",
                Password = "p",
                SipServer = "sipconnect.example",
                PublicSipHost = "203.0.113.9",
            },
            "test/1.0",
            registration,
            new NoopSignalingService(),
            new NoopSdpNegotiator(),
            iceAgent: null,
            SrtpPolicy.Optional,
            telemetry: null,
            NullLoggerFactory.Instance);

        channel.StartRegistration(_ => { });

        await PollUntil(() => registration.Requests.Count >= 1);
        await Task.Delay(150);

        // Override wins: no corrective churn, and every REGISTER carries the manual host.
        Assert.Single(registration.Requests);
        Assert.Equal("203.0.113.9", registration.Requests[0].PublicHost);

        channel.Dispose();
    }

    private static async Task PollUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }

    // --- Fakes ---

    private sealed class RecordingRegistrationService : ISipRegistrationService
    {
        private readonly string _observedHost;
        private readonly int _observedPort;
        private readonly List<SipRegistrationRequest> _requests = [];

        public RecordingRegistrationService(string observedHost, int observedPort)
        {
            _observedHost = observedHost;
            _observedPort = observedPort;
        }

        public IReadOnlyList<SipRegistrationRequest> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
        {
            lock (_requests)
                _requests.Add(request);

            return Task.FromResult(new SipRegistrationResult
            {
                CallId = "call-id",
                StatusCode = 200,
                EffectiveExpiresSeconds = 3600, // long so the refresh cycle never fires during the test
                ContactUri = "sip:u@host",
                Authenticated = true,
                NextCSeq = request.StartCSeq + 1,
                ObservedPublicHost = _observedHost,
                ObservedPublicPort = _observedPort,
            });
        }

        public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            RegisterAsync(request, ct);

        public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            RegisterAsync(request, ct);

        public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            RegisterAsync(request, ct);
    }

    private sealed class NoopSignalingService : ISipCallSignalingService
    {
        public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }
        public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted { add { } remove { } }

        public Task<ISipCallSession> InviteAsync(SipInviteRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class NoopSdpNegotiator : ISdpNegotiator
    {
        public string BuildDefaultSdp(IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? options = null) => "v=0";
        public string? TryBuildNegotiatedAnswer(string remoteOffer, IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? localOptions = null) => null;
        public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint) => null;
        public bool IsRemoteHoldSdp(string? sdp) => false;
    }
}
