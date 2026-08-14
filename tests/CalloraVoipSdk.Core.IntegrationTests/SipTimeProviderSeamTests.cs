using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// F003: the SIP signalling schedules — the registration refresh loop and the RFC 4028 session timer —
/// waited on the real clock, so a soak over many cycles cost real hours. A registration typically
/// refreshes every ~50 minutes and a session timer every ~15; a hundred cycles is a working day of
/// waiting, which is why long-running signalling behaviour was effectively untested.
///
/// Both now take a <see cref="TimeProvider"/> (default <see cref="TimeProvider.System"/>, so production
/// behaviour is unchanged). These tests are the proof that the seam actually rafts time: they drive
/// cycles that would take hours in wall-clock in a few milliseconds.
/// </summary>
public sealed class SipTimeProviderSeamTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task The_registration_loop_refreshes_when_the_fake_clock_reaches_the_interval()
    {
        var time = new FakeTimeProvider();
        var registration = new CountingRegistrationService(expiresSeconds: 3600);
        using var channel = NewChannel(registration, time);

        channel.StartRegistration(_ => { });
        await PollUntil(() => registration.RegisterCount >= 1);

        // An hour of registration lifetime, without an hour passing. Real time here would be a refresh
        // every ~54 minutes (the loop refreshes ahead of expiry), so this single step is what makes the
        // cycle observable at all.
        await AdvanceUntil(time, TimeSpan.FromSeconds(3600), () => registration.RegisterCount >= 2);

        Assert.True(registration.RegisterCount >= 2);
    }

    [Fact]
    public async Task Many_registration_cycles_run_without_real_time_passing()
    {
        var time = new FakeTimeProvider();
        var registration = new CountingRegistrationService(expiresSeconds: 3600);
        using var channel = NewChannel(registration, time);

        channel.StartRegistration(_ => { });
        await PollUntil(() => registration.RegisterCount >= 1);

        // Twenty cycles at a 3600 s interval is roughly 20 hours of wire time. Each step is awaited
        // through to the resulting REGISTER so the loop is genuinely driven, not merely fast-forwarded
        // past.
        for (var cycle = 2; cycle <= 21; cycle++)
            await AdvanceUntil(time, TimeSpan.FromSeconds(3600), () => registration.RegisterCount >= cycle);

        Assert.True(registration.RegisterCount >= 21);
    }

    [Fact]
    public async Task Without_advancing_the_fake_clock_no_refresh_happens()
    {
        // The counterpart to the tests above: they would also pass if the loop ignored the clock and
        // simply span. It does not — nothing moves until the clock does.
        var time = new FakeTimeProvider();
        var registration = new CountingRegistrationService(expiresSeconds: 3600);
        using var channel = NewChannel(registration, time);

        channel.StartRegistration(_ => { });
        await PollUntil(() => registration.RegisterCount >= 1);

        await Task.Delay(200); // real time passes; the fake clock does not
        Assert.Equal(1, registration.RegisterCount);
    }

    [Fact]
    public async Task The_session_timer_fires_its_refresh_on_the_injected_clock()
    {
        var time = new FakeTimeProvider();
        var refreshes = 0;
        var expired = 0;

        using var manager = new SipSessionTimerManager(
            NullLogger.Instance,
            _ => { Interlocked.Increment(ref refreshes); return Task.FromResult(true); },
            _ => { Interlocked.Increment(ref expired); return Task.CompletedTask; },
            time);

        // RFC 4028: as the refresher we re-INVITE ahead of the session interval — the manager keeps a
        // safety margin of interval/3, capped at 600 s, so 1800 s negotiates to a refresh at 1200 s.
        manager.ApplyNegotiation(intervalSeconds: 1800, localRefresher: true);
        await WaitForTimerToArm();

        time.Advance(TimeSpan.FromSeconds(1100));
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref refreshes)); // short of 1200 s: nothing yet

        time.Advance(TimeSpan.FromSeconds(100));

        await PollUntil(() => Volatile.Read(ref refreshes) >= 1);
        Assert.Equal(0, Volatile.Read(ref expired));
    }

    [Fact]
    public async Task The_session_timer_expires_the_dialog_when_the_peer_is_refresher_and_stays_silent()
    {
        var time = new FakeTimeProvider();
        var expired = 0;

        using var manager = new SipSessionTimerManager(
            NullLogger.Instance,
            _ => Task.FromResult(true),
            _ => { Interlocked.Increment(ref expired); return Task.CompletedTask; },
            time);

        manager.ApplyNegotiation(intervalSeconds: 1800, localRefresher: false);
        await WaitForTimerToArm();

        // As the non-refresher we wait out the full interval plus a jitter grace of interval/10 capped
        // at 30 s, so expiry is due at 1830 s — not at 1800.
        time.Advance(TimeSpan.FromSeconds(1800));
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref expired)); // the grace period is still running

        time.Advance(TimeSpan.FromSeconds(30));

        await PollUntil(() => Volatile.Read(ref expired) >= 1);
    }

    [Fact]
    public void Both_seams_default_to_the_system_clock()
    {
        // Production passes nothing, so the default must be real time — otherwise this change would
        // silently freeze every registration refresh in the field.
        using var channel = NewChannel(new CountingRegistrationService(expiresSeconds: 60), timeProvider: null);
        using var manager = new SipSessionTimerManager(
            NullLogger.Instance, _ => Task.FromResult(true), _ => Task.CompletedTask);

        Assert.NotNull(channel);
        Assert.NotNull(manager);
    }

    private static SipLineChannel NewChannel(ISipRegistrationService registration, TimeProvider? timeProvider) =>
        new(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            "test/1.0",
            registration,
            new NoopSignalingService(),
            new NoopSdpNegotiator(),
            iceAgent: null,
            SrtpPolicy.Optional,
            telemetry: null,
            NullLoggerFactory.Instance,
            timeProvider: timeProvider);

    /// <summary>
    /// Gives the schedule a moment to arm its timer before the clock is moved.
    /// </summary>
    /// <remarks>
    /// The two session-timer tests assert an exact boundary — nothing before the due instant, the callback
    /// after it — so they cannot use the self-healing stepping of <see cref="AdvanceUntil"/>: repeated
    /// steps would blur the very edge under test. They need the timer to exist before the first Advance,
    /// and the schedule starts asynchronously. The boundaries are kept a comfortable distance from the due
    /// instant (100 s of simulated time) so the assertion is about the schedule's arithmetic, not about
    /// thread timing.
    /// </remarks>
    private static Task WaitForTimerToArm() => Task.Delay(100);

    /// <summary>
    /// Advances the fake clock in <paramref name="step"/> increments until <paramref name="condition"/>
    /// holds.
    /// </summary>
    /// <remarks>
    /// A single Advance is not enough, and the reason is a race worth naming: the registration loop only
    /// arms its next timer <em>after</em> the REGISTER round-trip completes. Advancing inside that window
    /// moves the clock past a timer that does not exist yet, and the timer armed a moment later then waits
    /// for an instant that has already gone by — the cycle never fires and the test hangs until its
    /// timeout. Under a loaded machine (the full suite runs thousands of tests in parallel) that window is
    /// easily hit. Stepping repeatedly is self-healing: a step that lands in the gap is simply followed by
    /// another.
    /// </remarks>
    private static async Task AdvanceUntil(FakeTimeProvider time, TimeSpan step, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            time.Advance(step);
            await Task.Delay(5);
        }

        Assert.Fail($"Condition was not met within {PollTimeout.TotalSeconds:F0}s of advancing the fake clock.");
    }

    private static async Task PollUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(5);
        }

        Assert.Fail($"Condition was not met within {PollTimeout.TotalSeconds:F0}s.");
    }

    private sealed class CountingRegistrationService(int expiresSeconds) : ISipRegistrationService
    {
        private int _registerCount;

        public int RegisterCount => Volatile.Read(ref _registerCount);

        public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _registerCount);
            return Task.FromResult(Result(request, expiresSeconds));
        }

        public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result(request, 0));

        public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            UnregisterAsync(request, ct);

        public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            RegisterAsync(request, ct);

        private static SipRegistrationResult Result(SipRegistrationRequest request, int expires) => new()
        {
            CallId = "call-id",
            StatusCode = 200,
            EffectiveExpiresSeconds = expires,
            ContactUri = "sip:u@host",
            Authenticated = true,
            NextCSeq = request.StartCSeq + 1,
        };
    }

    private sealed class NoopSignalingService : ISipCallSignalingService
    {
        public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }
        public event EventHandler<SipIncomingMessageEventArgs>? IncomingMessage { add { } remove { } }
        public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted { add { } remove { } }

        public Task<ISipCallSession> InviteAsync(SipInviteRequest request, Action<ISipCallSession>? onSessionCreated = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> SendMessageAsync(SipMessageRequest request, CancellationToken ct = default) => Task.FromResult(200);

        public Task<SipPublishResult> PublishAsync(SipPublishRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SipPublishResult(200, null, 0));

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
