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
/// Behavioural proof that a registered SIP line stays registered:
/// (a) proactive re-REGISTER before the confirmed expiry lapses,
/// (b) reconnect after a transient failure,
/// (c) fully manual behaviour when <see cref="ReregisterOptions.AutoReregister"/> is disabled,
/// and clean cancellation of scheduled refreshes on dispose.
///
/// The refresh timing floor inside <c>SipLineChannel</c> is 5 seconds
/// (<c>ComputeRefreshDelay</c> clamps to a 5 s minimum), so the timed assertions below use
/// generous timeouts and complete as soon as the observable REGISTER fires.
/// </summary>
public sealed class SipLineChannelRegistrationTests
{
    private static readonly TimeSpan RefreshObservationTimeout = TimeSpan.FromSeconds(15);

    // Longer than the 5 s refresh floor: if a proactive refresh were (wrongly) scheduled it
    // would have fired within this window, so a still-single REGISTER count proves it did not.
    private static readonly TimeSpan NoRefreshWindow = TimeSpan.FromSeconds(6.5);

    /// <summary>
    /// Test 1: after a successful REGISTER a second REGISTER is issued proactively before the
    /// confirmed expiry lapses, reusing the same Call-ID and an advanced CSeq (RFC 3261 §10.2.4).
    /// Proves the binding is kept alive.
    /// </summary>
    [Fact]
    public async Task SuccessfulRegistration_ProactivelyRefreshesBeforeExpiry()
    {
        var registration = new FakeRegistrationService(
            (_, request) => FakeRegistrationService.SuccessResult(request, effectiveExpiresSeconds: 6));
        var account = CreateAccount(registrationExpiry: 6, ReregisterOptions.Default);
        using var channel = CreateChannel(account, registration);

        var states = new StateRecorder();
        channel.StartRegistration(states.Record);

        var refreshed = await registration.WaitForRegisterCountAsync(2, RefreshObservationTimeout);

        Assert.True(refreshed, "Expected a proactive second REGISTER before the expiry lapsed.");
        Assert.Contains(LineState.Registered, states.Snapshot());

        var requests = registration.RegisterRequests;
        // The refresh reuses the persisted binding: same Call-ID, next CSeq.
        Assert.Equal(requests[0].ExistingCallId ?? "fake-call-id", requests[1].ExistingCallId);
        Assert.True(requests[1].StartCSeq > requests[0].StartCSeq,
            "Refresh REGISTER must advance the CSeq of the previous binding.");
    }

    /// <summary>
    /// Test 2: the refresh schedule uses the registrar-confirmed expiry, not the requested one.
    /// The account requests a very large expiry (3600 s) but the fake registrar confirms only 6 s.
    /// A refresh observed within the short window can only happen if the confirmed value was used
    /// (0.8 × 3600 ≈ 2880 s would never fire inside the test budget).
    /// </summary>
    [Fact]
    public async Task Refresh_UsesRegistrarConfirmedExpiry_NotRequested()
    {
        var registration = new FakeRegistrationService(
            (_, request) => FakeRegistrationService.SuccessResult(request, effectiveExpiresSeconds: 6));
        var account = CreateAccount(registrationExpiry: 3600, ReregisterOptions.Default);
        using var channel = CreateChannel(account, registration);

        channel.StartRegistration(_ => { });

        var refreshed = await registration.WaitForRegisterCountAsync(2, RefreshObservationTimeout);

        Assert.True(refreshed,
            "Refresh must be driven by the confirmed 6 s expiry, not the requested 3600 s.");
    }

    /// <summary>
    /// Test 3: a transient REGISTER failure after a prior success transitions the line to
    /// <see cref="LineState.Reconnecting"/>, fires <c>onReconnecting</c>, and retries successfully.
    /// </summary>
    [Fact]
    public async Task TransientFailureAfterSuccess_EntersReconnectingAndRetries()
    {
        // 1st REGISTER: success (short expiry → refresh at the 5 s floor).
        // 2nd REGISTER (the refresh): transient 503-style failure → Reconnecting + backoff.
        // 3rd REGISTER (the retry): success again.
        var registration = new FakeRegistrationService((index, request) =>
        {
            if (index == 2)
                throw new SipRegistrationFailedException("transient", 503, "Service Unavailable");
            return FakeRegistrationService.SuccessResult(request, effectiveExpiresSeconds: 6);
        });
        var options = new ReregisterOptions
        {
            AutoReregister = true,
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200)
        };
        var account = CreateAccount(registrationExpiry: 6, options);
        using var channel = CreateChannel(account, registration);

        var states = new StateRecorder();
        var reconnectingAttempts = new List<int>();
        channel.StartRegistration(
            states.Record,
            onReconnecting: attempt => { lock (reconnectingAttempts) reconnectingAttempts.Add(attempt); });

        var retried = await registration.WaitForRegisterCountAsync(3, RefreshObservationTimeout);

        Assert.True(retried, "Expected a retry REGISTER after the transient failure.");
        Assert.Contains(LineState.Reconnecting, states.Snapshot());
        lock (reconnectingAttempts)
        {
            Assert.NotEmpty(reconnectingAttempts);
            Assert.Equal(1, reconnectingAttempts[0]);
        }
    }

    /// <summary>
    /// Test 4: with <see cref="ReregisterOptions.AutoReregister"/> disabled the line registers once
    /// and issues no proactive refresh — even after the refresh window has elapsed.
    /// </summary>
    [Fact]
    public async Task AutoReregisterDisabled_DoesNotProactivelyRefresh()
    {
        var registration = new FakeRegistrationService(
            (_, request) => FakeRegistrationService.SuccessResult(request, effectiveExpiresSeconds: 6));
        var account = CreateAccount(registrationExpiry: 6, ReregisterOptions.Disabled);
        using var channel = CreateChannel(account, registration);

        var states = new StateRecorder();
        channel.StartRegistration(states.Record);

        // Wait for the first REGISTER, then wait past the refresh floor.
        Assert.True(await registration.WaitForRegisterCountAsync(1, RefreshObservationTimeout));
        await Task.Delay(NoRefreshWindow);

        Assert.Equal(1, registration.RegisterCallCount);
        Assert.Equal(LineState.Registered, states.Last());
    }

    /// <summary>
    /// Test 4b: with <see cref="ReregisterOptions.AutoReregister"/> disabled a transient failure is
    /// not retried; the line reports <see cref="LineState.RegistrationFailed"/> and stops.
    /// </summary>
    [Fact]
    public async Task AutoReregisterDisabled_DoesNotRetryOnFailure()
    {
        var registration = new FakeRegistrationService(
            (_, _) => throw new SipRegistrationFailedException("transient", 503, "Service Unavailable"));
        var account = CreateAccount(registrationExpiry: 6, ReregisterOptions.Disabled);
        using var channel = CreateChannel(account, registration);

        var states = new StateRecorder();
        channel.StartRegistration(states.Record);

        Assert.True(await registration.WaitForRegisterCountAsync(1, RefreshObservationTimeout));
        // Give any (unexpected) retry ample time to happen.
        await Task.Delay(NoRefreshWindow);

        Assert.Equal(1, registration.RegisterCallCount);
        Assert.Contains(LineState.RegistrationFailed, states.Snapshot());
    }

    /// <summary>
    /// Test 5: disposing the line cancels the scheduled refresh — no further REGISTER is issued
    /// after dispose, and a best-effort unREGISTER is attempted.
    /// </summary>
    [Fact]
    public async Task Dispose_CancelsScheduledRefresh()
    {
        var registration = new FakeRegistrationService(
            (_, request) => FakeRegistrationService.SuccessResult(request, effectiveExpiresSeconds: 6));
        var account = CreateAccount(registrationExpiry: 6, ReregisterOptions.Default);
        var channel = CreateChannel(account, registration);

        channel.StartRegistration(_ => { });

        // Dispose well before the 5 s refresh floor elapses.
        Assert.True(await registration.WaitForRegisterCountAsync(1, RefreshObservationTimeout));
        channel.Dispose();

        var countAtDispose = registration.RegisterCallCount;
        await Task.Delay(NoRefreshWindow);

        Assert.Equal(countAtDispose, registration.RegisterCallCount);
        Assert.True(registration.UnregisterCallCount >= 1,
            "Dispose should attempt a best-effort unREGISTER.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static SipAccount CreateAccount(int registrationExpiry, ReregisterOptions reregister) =>
        new()
        {
            Username = "alice",
            Password = "secret",
            SipServer = "pbx.example.com",
            Transport = SipTransport.Udp,
            RegistrationExpiry = registrationExpiry,
            Reregister = reregister
        };

    private static SipLineChannel CreateChannel(SipAccount account, FakeRegistrationService registration) =>
        new(
            account,
            userAgent: "CalloraVoipSdk-Test/1.0",
            registration,
            new FakeCallSignalingService(),
            new StubSdpNegotiator(),
            iceAgent: null,
            globalSrtpPolicy: SrtpPolicy.Disabled,
            telemetry: null,
            NullLoggerFactory.Instance);
}

/// <summary>
/// Thread-safe recorder for <see cref="LineState"/> transitions observed during a test.
/// </summary>
internal sealed class StateRecorder
{
    private readonly object _sync = new();
    private readonly List<LineState> _states = new();

    /// <summary>Records one observed line-state transition.</summary>
    public void Record(LineState state)
    {
        lock (_sync)
            _states.Add(state);
    }

    /// <summary>Returns an immutable snapshot of the recorded transitions.</summary>
    public IReadOnlyList<LineState> Snapshot()
    {
        lock (_sync)
            return _states.ToArray();
    }

    /// <summary>Returns the most recently recorded transition.</summary>
    public LineState Last()
    {
        lock (_sync)
            return _states[^1];
    }
}

/// <summary>
/// Deterministic fake registrar that records REGISTER attempts and lets a test decide the
/// per-attempt outcome (success result or thrown failure).
/// </summary>
internal sealed class FakeRegistrationService : ISipRegistrationService
{
    private readonly object _sync = new();
    private readonly List<SipRegistrationRequest> _registerRequests = new();
    private readonly Func<int, SipRegistrationRequest, SipRegistrationResult> _registerBehavior;
    private int _unregisterCount;

    /// <summary>
    /// Creates the fake with a behaviour delegate invoked per REGISTER attempt.
    /// The first argument is the one-based attempt index; the delegate may throw to simulate failure.
    /// </summary>
    public FakeRegistrationService(Func<int, SipRegistrationRequest, SipRegistrationResult> registerBehavior)
        => _registerBehavior = registerBehavior ?? throw new ArgumentNullException(nameof(registerBehavior));

    /// <summary>Total number of REGISTER attempts observed.</summary>
    public int RegisterCallCount
    {
        get { lock (_sync) return _registerRequests.Count; }
    }

    /// <summary>Total number of unREGISTER attempts observed.</summary>
    public int UnregisterCallCount => Volatile.Read(ref _unregisterCount);

    /// <summary>Immutable snapshot of the REGISTER requests observed so far.</summary>
    public IReadOnlyList<SipRegistrationRequest> RegisterRequests
    {
        get { lock (_sync) return _registerRequests.ToArray(); }
    }

    /// <inheritdoc />
    public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
    {
        int index;
        lock (_sync)
        {
            _registerRequests.Add(request);
            index = _registerRequests.Count;
        }

        try
        {
            return Task.FromResult(_registerBehavior(index, request));
        }
        catch (Exception ex)
        {
            return Task.FromException<SipRegistrationResult>(ex);
        }
    }

    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _unregisterCount);
        return Task.FromResult(SuccessResult(request, effectiveExpiresSeconds: 0));
    }

    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _unregisterCount);
        return Task.FromResult(SuccessResult(request, effectiveExpiresSeconds: 0));
    }

    /// <inheritdoc />
    public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default)
        => Task.FromResult(SuccessResult(request, effectiveExpiresSeconds: 0));

    /// <summary>Polls until the REGISTER count reaches <paramref name="target"/> or the timeout lapses.</summary>
    public async Task<bool> WaitForRegisterCountAsync(int target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (RegisterCallCount >= target)
                return true;
            await Task.Delay(15).ConfigureAwait(false);
        }

        return RegisterCallCount >= target;
    }

    /// <summary>Builds a 200 OK style result, reusing the caller's Call-ID for binding refreshes.</summary>
    public static SipRegistrationResult SuccessResult(
        SipRegistrationRequest request,
        int effectiveExpiresSeconds) =>
        new()
        {
            CallId = string.IsNullOrWhiteSpace(request.ExistingCallId) ? "fake-call-id" : request.ExistingCallId!,
            StatusCode = 200,
            EffectiveExpiresSeconds = effectiveExpiresSeconds,
            ContactUri = $"sip:{request.Username}@127.0.0.1:5060",
            Authenticated = false,
            NextCSeq = (request.StartCSeq > 0 ? request.StartCSeq : 1) + 1,
            RegisteredBindings = Array.Empty<SipRegistrationBinding>()
        };
}

/// <summary>
/// Minimal call-signaling stub for line-channel construction; call flows are not exercised here.
/// </summary>
#pragma warning disable CS0067 // Events are required by the interface but never raised in these tests.
internal sealed class FakeCallSignalingService : ISipCallSignalingService
{
    /// <inheritdoc />
    public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite;

    /// <inheritdoc />
    public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted;

    /// <inheritdoc />
    public Task<ISipCallSession> InviteAsync(SipInviteRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public void Dispose() { }
}
#pragma warning restore CS0067

/// <summary>
/// SDP negotiator stub; registration never touches the media path.
/// </summary>
internal sealed class StubSdpNegotiator : ISdpNegotiator
{
    /// <inheritdoc />
    public string BuildDefaultSdp(IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? options = null)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public string? TryBuildNegotiatedAnswer(
        string remoteOffer,
        IPEndPoint localEndPoint,
        bool hold,
        SdpMediaNegotiationOptions? localOptions = null)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public CallMediaParameters? TryParseMediaParameters(
        string remoteSdp,
        IPEndPoint localEndPoint,
        string? localSdp = null)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public bool IsRemoteHoldSdp(string? sdp) => throw new NotSupportedException();
}
