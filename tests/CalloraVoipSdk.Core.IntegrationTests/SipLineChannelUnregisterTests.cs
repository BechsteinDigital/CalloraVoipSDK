using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Unregistering a line must remove the existing binding by re-using the registration's own
/// Call-ID and next CSeq (RFC 3261 §10.2.2), not a fresh Call-ID + CSeq 1 — otherwise the
/// registrar does not recognise it as removing the binding, which lingers until expiry and
/// forks inbound INVITEs into a dead second binding.
/// </summary>
public sealed class SipLineChannelUnregisterTests
{
    [Fact]
    public async Task Unregister_reuses_the_registration_call_id_and_next_cseq()
    {
        var registration = new CapturingRegistrationService();
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
        await PollUntil(() => registration.RegisterCount >= 1);
        await Task.Delay(100); // let the channel persist Call-ID/CSeq from the 200 OK

        channel.StopRegistration(); // best-effort unregister

        await PollUntil(() => registration.LastUnregister is not null);

        Assert.Equal("call-id", registration.LastUnregister!.ExistingCallId); // reuse, not fresh
        Assert.Equal(2, registration.LastUnregister.StartCSeq);               // continues (1 → 2)

        channel.Dispose();
    }

    [Fact]
    public async Task StopRegistrationAsync_awaits_the_deregister_round_trip()
    {
        var registration = new CapturingRegistrationService();
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
        await PollUntil(() => registration.RegisterCount >= 1);
        await Task.Delay(100); // let the channel persist Call-ID/CSeq from the 200 OK

        await channel.StopRegistrationAsync(); // awaited de-register (HARD-E1)

        // No PollUntil: the awaited path guarantees the REGISTER Expires:0 completed before returning.
        Assert.NotNull(registration.LastUnregister);
        Assert.Equal("call-id", registration.LastUnregister!.ExistingCallId); // reuse, not fresh
        Assert.Equal(2, registration.LastUnregister.StartCSeq);               // continues (1 → 2)

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

    private sealed class CapturingRegistrationService : ISipRegistrationService
    {
        private int _registerCount;
        public int RegisterCount => Volatile.Read(ref _registerCount);
        public SipRegistrationRequest? LastUnregister { get; private set; }

        public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _registerCount);
            return Task.FromResult(new SipRegistrationResult
            {
                CallId = "call-id",
                StatusCode = 200,
                EffectiveExpiresSeconds = 3600, // long → refresh cycle never fires during the test
                ContactUri = "sip:u@host",
                Authenticated = true,
                NextCSeq = request.StartCSeq + 1,
            });
        }

        public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
        {
            LastUnregister = request;
            return Task.FromResult(new SipRegistrationResult
            {
                CallId = "call-id",
                StatusCode = 200,
                EffectiveExpiresSeconds = 0,
                ContactUri = "sip:u@host",
                Authenticated = true,
                NextCSeq = request.StartCSeq + 1,
            });
        }

        public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            UnregisterAsync(request, ct);

        public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            RegisterAsync(request, ct);
    }

    private sealed class NoopSignalingService : ISipCallSignalingService
    {
        public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }
        public event EventHandler<SipIncomingMessageEventArgs>? IncomingMessage { add { } remove { } }
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
