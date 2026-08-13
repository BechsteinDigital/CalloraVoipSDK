using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #256: a 202 Accepted says the peer took the REFER, not that the transfer worked. The outcome
/// arrives later as a NOTIFY carrying a <c>message/sipfrag</c> status line (RFC 3515 §2.4.4,
/// RFC 5589 §7). Resolving at the 202 declared the call terminated while the PBX was still
/// re-bridging the transferee — which cost the transferee its dialog — and reported success for
/// transfers that then failed.
/// </summary>
public sealed class SipReferCompletionTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);

    private static Task<bool> RunAsync(NotifyingSession session, TimeSpan? timeout = null) =>
        SipReferCompletion.SendAndAwaitAsync(
            session,
            "sip:target@example.com",
            timeout ?? TimeSpan.FromSeconds(5),
            NullLogger.Instance,
            CancellationToken.None);

    // ── the outcome decides, not the 202 ─────────────────────────────────────

    [Fact]
    public async Task A_sipfrag_200_completes_the_transfer_successfully()
    {
        var session = new NotifyingSession();
        var transfer = RunAsync(session);

        await session.WaitForReferAsync();
        session.RaiseNotify("refer", "active;expires=60", "message/sipfrag", "SIP/2.0 200 OK");

        Assert.True(await transfer);
    }

    [Fact]
    public async Task A_failed_transfer_is_reported_as_failure()
    {
        // The consequence: Call.AttendedTransferAsync leaves the call Connected instead of declaring
        // it terminated. Before, a transfer the peer rejected still looked like a success.
        var session = new NotifyingSession();
        var transfer = RunAsync(session);

        await session.WaitForReferAsync();
        session.RaiseNotify("refer", "terminated;reason=noresource", "message/sipfrag", "SIP/2.0 486 Busy Here");

        Assert.False(await transfer);
    }

    [Fact]
    public async Task Progress_notifies_do_not_end_the_wait()
    {
        // "SIP/2.0 100 Trying" is progress, not an outcome (RFC 3515 §2.4.5). Treating it as one would
        // reintroduce exactly the race this fixes.
        var session = new NotifyingSession();
        var transfer = RunAsync(session);

        await session.WaitForReferAsync();
        session.RaiseNotify("refer", "active", "message/sipfrag", "SIP/2.0 100 Trying");
        Assert.False(transfer.IsCompleted);

        session.RaiseNotify("refer", "terminated;reason=noresource", "message/sipfrag", "SIP/2.0 200 OK");
        Assert.True(await transfer);
    }

    [Fact]
    public async Task A_notify_arriving_before_the_refer_returns_is_not_missed()
    {
        // A fast peer can deliver the NOTIFY before the 202 has been processed. Subscribing after the
        // send would lose it and stall until the timeout.
        var session = new NotifyingSession();
        session.RaiseNotifyDuringRefer("refer", "terminated;reason=noresource", "message/sipfrag", "SIP/2.0 200 OK");

        Assert.True(await RunAsync(session));
    }

    // ── the cases where waiting must not become blocking ─────────────────────

    [Fact]
    public async Task Silence_resolves_as_success_once_the_timeout_elapses()
    {
        // A peer that suppresses the subscription (RFC 4488) never reports. Failing closed on silence
        // would break every transfer against such a peer; the 202 is all we get, and it did accept.
        var session = new NotifyingSession();

        Assert.True(await RunAsync(session, ShortTimeout));
    }

    [Fact]
    public async Task A_rejected_refer_fails_immediately_without_waiting()
    {
        var session = new NotifyingSession { ReferAccepted = false };

        var started = DateTimeOffset.UtcNow;
        Assert.False(await RunAsync(session, TimeSpan.FromSeconds(30)));

        // No 202, nothing to wait for — it must not sit out the timeout.
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_terminated_subscription_without_a_sipfrag_ends_the_wait()
    {
        // Nothing further will be reported, so holding out for the full timeout only adds latency.
        var session = new NotifyingSession();
        var transfer = RunAsync(session, TimeSpan.FromSeconds(30));

        await session.WaitForReferAsync();
        session.RaiseNotify("refer", "terminated;reason=timeout", contentType: null, body: null);

        Assert.True(await transfer);
    }

    [Fact]
    public async Task A_notify_for_another_event_package_is_ignored()
    {
        var session = new NotifyingSession();
        var transfer = RunAsync(session, ShortTimeout);

        await session.WaitForReferAsync();
        session.RaiseNotify("presence", "active", "application/pidf+xml", "<presence/>");
        Assert.False(transfer.IsCompleted);

        Assert.True(await transfer);   // resolves via the timeout, not via that NOTIFY
    }

    [Fact]
    public async Task The_handler_is_detached_once_the_transfer_resolves()
    {
        // The session outlives the transfer; a handler left attached would leak and keep resolving
        // against a dead TaskCompletionSource.
        var session = new NotifyingSession();
        var transfer = RunAsync(session);

        await session.WaitForReferAsync();
        session.RaiseNotify("refer", "terminated", "message/sipfrag", "SIP/2.0 200 OK");
        await transfer;

        Assert.Equal(0, session.NotifyHandlerCount);
    }

    // ── sipfrag status line (RFC 3420) ───────────────────────────────────────

    [Theory]
    [InlineData("SIP/2.0 200 OK", 200)]
    [InlineData("SIP/2.0 100 Trying", 100)]
    [InlineData("SIP/2.0 486 Busy Here", 486)]
    [InlineData("SIP/2.0 200 OK\r\n", 200)]
    [InlineData("SIP/2.0 202", 202)]                    // no reason phrase
    [InlineData("  SIP/2.0 200 OK", 200)]
    public void A_status_line_yields_its_code(string body, int expected)
    {
        Assert.Equal(expected, SipReferCompletion.TryParseSipfragStatus("message/sipfrag", body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SIP/2.0")]                             // no code at all
    [InlineData("SIP/2.0 abc OK")]
    [InlineData("SIP/2.0 999 Nonsense")]                // outside the response-code range
    [InlineData("garbage")]
    public void A_body_that_is_not_a_status_line_yields_nothing(string body)
    {
        Assert.Null(SipReferCompletion.TryParseSipfragStatus("message/sipfrag", body));
    }

    [Fact]
    public void A_body_of_another_content_type_is_not_read_as_a_status_line()
    {
        Assert.Null(SipReferCompletion.TryParseSipfragStatus("application/pidf+xml", "SIP/2.0 200 OK"));
    }

    [Fact]
    public void A_missing_content_type_is_tolerated()
    {
        // Peers do omit it, and the status line is self-describing.
        Assert.Equal(200, SipReferCompletion.TryParseSipfragStatus(null, "SIP/2.0 200 OK"));
    }

    /// <summary>Established session that can raise NOTIFYs and counts its attached handlers.</summary>
    private sealed class NotifyingSession : ISipCallSession
    {
        private readonly TaskCompletionSource _referSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<SipNotifyReceivedEventArgs>? _notify;
        private (string Event, string State, string? ContentType, string? Body)? _raiseDuringRefer;

        public bool ReferAccepted { get; init; } = true;

        public int NotifyHandlerCount => _notify?.GetInvocationList().Length ?? 0;

        public Task WaitForReferAsync() => _referSent.Task;

        public void RaiseNotify(string eventType, string subscriptionState, string? contentType, string? body) =>
            _notify?.Invoke(
                this,
                new SipNotifyReceivedEventArgs(
                    eventType,
                    subscriptionState,
                    subscriptionState.StartsWith("terminated", StringComparison.OrdinalIgnoreCase),
                    contentType,
                    body));

        /// <summary>Delivers the NOTIFY from inside SendReferAsync, before it returns.</summary>
        public void RaiseNotifyDuringRefer(string eventType, string subscriptionState, string? contentType, string? body) =>
            _raiseDuringRefer = (eventType, subscriptionState, contentType, body);

        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived
        {
            add => _notify += value;
            remove => _notify -= value;
        }

        public Task<bool> SendReferAsync(
            string referTo, string? referredBy = null, bool suppressSubscription = false, CancellationToken ct = default)
        {
            if (_raiseDuringRefer is { } pending)
                RaiseNotify(pending.Event, pending.State, pending.ContentType, pending.Body);

            _referSent.TrySetResult();
            return Task.FromResult(ReferAccepted);
        }

        public string CallId => "call-1";
        public string RemoteUri => "sip:bob@example.com";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string? LocalTag => "local";
        public string? RemoteTag => "remote";
        public SipDialogState State => SipDialogState.Established;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => false;
        public string? RemoteAssertedIdentity => null;
        public string? RemoteSdp => null;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default) => Task.CompletedTask;
        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, int durationMs = 160, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SendOptionsAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SendSubscribeAsync(
            string eventType, int expiresSeconds, string? acceptHeader, string? body, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> SendNotifyAsync(
            string eventType, string subscriptionState, string? contentType, string? body, CancellationToken ct = default) =>
            Task.FromResult(true);

        public void Dispose() { }
    }
}
