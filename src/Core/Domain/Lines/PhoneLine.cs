using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Messages;

namespace CalloraVoipSdk.Core.Domain.Lines;

internal sealed class PhoneLine : IPhoneLine, IDisposable
{
    private readonly ILineChannel                      _channel;
    private readonly ICallRegistry                     _callRegistry;
    private readonly ILoggerFactory                    _loggerFactory;
    private readonly int                               _maxCalls;
    private readonly ILogger<PhoneLine>                _logger;
    private readonly Action<ICall, ICallChannel>?      _onCallCreated;
    private readonly object                            _sync    = new();

    // Per-line active call counter — the registry's Active includes calls from all lines,
    // so we track our own count to enforce per-line limits correctly.
    private int                          _activeLineCallCount;

    private LineState                    _state   = LineState.Unregistered;
    private bool                         _disposed;

    public LineId     LineId   { get; } = LineId.New();
    public SipAccount Account  { get; }
    public LineState  State    { get { lock (_sync) return _state; } }

    /// <inheritdoc />
    public IReadOnlyList<string> AnnouncedAddresses => _channel.AnnouncedAddresses;

    public event EventHandler<LineStateChangedEventArgs>?    StateChanged;
    public event EventHandler<IncomingCallEventArgs>?         IncomingCall;
    public event EventHandler<OutboundCallRingingEventArgs>? OutboundCallRinging;
    public event EventHandler<IncomingMessageEventArgs>?      IncomingMessage;
    public event EventHandler<LineReconnectingEventArgs>?    LineReconnecting;
    public event EventHandler<LineReconnectFailedEventArgs>? LineReconnectFailed;

    // The terminal reconnect/registration failure reason, captured as state so an observer that only
    // sees LineState.Failed (e.g. a convenience connect that subscribed after a fast failure already
    // fired the event) can surface it race-free. Written before the Failed transition (F005b).
    private volatile LineReconnectFailedEventArgs? _lastReconnectFailure;

    /// <inheritdoc />
    public LineReconnectFailedEventArgs? LastReconnectFailure => _lastReconnectFailure;

    internal PhoneLine(
        SipAccount                    account,
        ILineChannel                  channel,
        ICallRegistry                 callRegistry,
        int                           maxCalls,
        ILoggerFactory                loggerFactory,
        Action<ICall, ICallChannel>?  onCallCreated = null)
    {
        // Cross-property configuration checks run once, here, so a contradictory account is rejected while
        // the caller is still holding it — not on the first retry, hours into a call (#165 P3-11).
        account.Validate();

        Account         = account;
        _channel        = channel;
        _callRegistry   = callRegistry;
        _maxCalls       = maxCalls;
        _loggerFactory  = loggerFactory;
        _onCallCreated  = onCallCreated;
        _logger         = loggerFactory.CreateLogger<PhoneLine>();

        _channel.SetInboundHandler(HandleInbound);
        _channel.SetMessageHandler(HandleInboundMessage);
    }

    internal void StartRegistration()
    {
        // An IP-authenticated trunk is recognised by source address; the provider expects no REGISTER and
        // may reject one. Go straight to the operational state instead of sending a request that would be
        // wrong on the wire — this is the only path to LineState.Ready.
        if (!Account.Register)
        {
            TransitionTo(LineState.Ready);
            return;
        }

        _channel.StartRegistration(
            TransitionTo,
            onReconnecting: attempt =>
            {
                var handlers = LineReconnecting;
                handlers?.Invoke(this, new LineReconnectingEventArgs(attempt, this));
            },
            onReconnectFailed: (reason, attemptCount) =>
            {
                // Capture the failure as state BEFORE raising the event and before the channel's
                // subsequent LineState.Failed transition, so a consumer that only observes the terminal
                // Failed state surfaces it race-free even if it missed the event (F005b).
                var args = new LineReconnectFailedEventArgs(reason, attemptCount, this);
                _lastReconnectFailure = args;
                LineReconnectFailed?.Invoke(this, args);
            });
    }

    /// <summary>
    /// Whether the line may place and receive calls: registered, or operational without a registration
    /// (<see cref="SipAccount.Register"/> = <see langword="false"/>).
    /// </summary>
    private bool IsOperational => State is LineState.Registered or LineState.Ready;

    // ── IPhoneLine ────────────────────────────────────────────────────────────
    public async Task<ICall> DialAsync(
        string targetUri, DialOptions? options = null, CancellationToken ct = default)
    {
        options ??= DialOptions.Default;

        if (!IsOperational)
            throw new InvalidOperationException(
                Account.Register
                    ? $"Line [{Account.Username}] is not registered."
                    : $"Line [{Account.Username}] is not ready (state {State}).");

        // Reserve a per-line call slot atomically BEFORE building the call, so N concurrent dials can
        // never all pass a stale read of the counter and overshoot the cap (increment-then-rollback).
        if (!TryReserveCallSlot())
            throw new InvalidOperationException(
                $"Max concurrent calls ({_maxCalls}) reached on line [{Account.Username}].");

        Call call;
        ICallChannel channel;
        try
        {
            channel = _channel.PrepareOutboundChannel(options);
            call = CreateCall(CallId.New(), CallDirection.Outbound, targetUri, channel);
        }
        catch
        {
            // Setup faulted before a Call took ownership of the reservation (CreateCall wires the matching
            // terminate-release). Release the slot here so a failed dial never leaks per-line capacity.
            ReleaseCallSlot();
            throw;
        }

        _callRegistry.Register(call);
        call.TransitionTo(CallState.Dialing);

        // Surface the call once it reaches Ringing (early dialog), while StartOutboundDialAsync still
        // awaits the 200 OK. Fires at most once; detaches itself.
        EventHandler<CallStateChangedEventArgs>? onRinging = null;
        onRinging = (_, e) =>
        {
            if (e.NewState != CallState.Ringing) return;
            call.StateChanged -= onRinging;
            OutboundCallRinging?.Invoke(this, new OutboundCallRingingEventArgs(call));
        };
        call.StateChanged += onRinging;

        try
        {
            await _channel.StartOutboundDialAsync(channel, targetUri, options, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && call.State != CallState.Terminated)
        {
            // Caller cancelled while the outbound INVITE was still pending/ringing. A plain local
            // teardown would leave the peer ringing until its own timeout — RFC 3261 §9.1 requires a
            // CANCEL for the in-flight INVITE. Route the abort through the call's hangup, which the
            // signaling channel maps to a CANCEL for an Inviting/Ringing outbound dialog (487), then
            // return the now-terminated call so the caller/convenience layer keeps a handle instead of
            // losing it to a rethrow. The channel hangup runs on its own uncancelled token, so the
            // CANCEL is not itself aborted by the already-cancelled caller token.
            _logger.LogDebug(ex, "Outbound dial to {Uri} cancelled on [{User}]; sending CANCEL.", targetUri, Account.Username);
            await CancelPendingDialAsync(call).ConfigureAwait(false);
            return call;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbound dial to {Uri} failed on [{User}]", targetUri, Account.Username);

            // A SIP final-response rejection (486/408/480/603/…) drives the registered call to Terminated
            // WITH a CallTerminationReason via the session→channel StateChanged path (SipCoreCallChannel
            // .BuildTerminationReason) before the dial method's exception unwinds here. In that case the
            // terminated call carries the outcome, so return it instead of rethrowing — this is what lets
            // DialAndWaitUntilConnectedAsync surface result.Call.TerminationReason (issue #103). A dial that
            // did NOT terminate the call (a genuine transport/network fault, or a caller cancellation/timeout
            // that must remain distinguishable at the convenience layer) still terminates locally and rethrows.
            if (call.State == CallState.Terminated)
                return call;

            call.TransitionTo(CallState.Terminated);
            throw;
        }
        finally
        {
            // Idempotent: the self-detach at the Ringing fire may already have run. Also covers the
            // direct Dialing→Connected success path (no Ringing) so no dead handler leaks onto
            // call.StateChanged. StartOutboundDialAsync returns after any Ringing transition, so the
            // handler is only removed once it has served its purpose.
            call.StateChanged -= onRinging;
        }

        return call;
    }

    public Task SendMessageAsync(string targetUri, string body, string contentType = "text/plain", CancellationToken ct = default)
        => _channel.SendMessageAsync(targetUri, body, contentType, ct);

    /// <inheritdoc />
    public Task<Subscriptions.ISipSubscription> SubscribeAsync(
        string eventType,
        string targetUri,
        int expiresSeconds = 300,
        string? accept = null,
        CancellationToken ct = default)
        => _channel.SubscribeAsync(eventType, targetUri, expiresSeconds, accept, ct);

    public Task<Publications.PublishResult> PublishAsync(string eventType, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default)
        => _channel.PublishAsync(eventType, body, contentType, expiresSeconds, ct: ct);

    /// <summary>
    /// Refreshes a prior publication's lifetime via SIP PUBLISH with SIP-If-Match (RFC 3903 §4.1), sending an
    /// empty body so the server retains the existing event state. The <paramref name="etag"/> is the SIP-ETag
    /// from a prior <see cref="PublishAsync"/>/refresh; returns the new SIP-ETag and granted lifetime.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to refresh.</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the refresh.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    public Task<Publications.PublishResult> RefreshPublicationAsync(string eventType, string etag, int expiresSeconds = 3600, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        return _channel.PublishAsync(eventType, string.Empty, "text/plain", expiresSeconds, ifMatch: etag, ct);
    }

    /// <summary>
    /// Replaces a prior publication's body via SIP PUBLISH with SIP-If-Match (RFC 3903 §4.1). The
    /// <paramref name="etag"/> is the SIP-ETag from a prior <see cref="PublishAsync"/>/refresh; returns the new
    /// SIP-ETag and granted lifetime.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to modify.</param>
    /// <param name="body">The replacement event-state document to publish (for example a PIDF body).</param>
    /// <param name="contentType">The body's MIME type; defaults to <c>text/plain</c>.</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the modify.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    public Task<Publications.PublishResult> ModifyPublicationAsync(string eventType, string etag, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        return _channel.PublishAsync(eventType, body, contentType, expiresSeconds, ifMatch: etag, ct);
    }

    /// <summary>
    /// Removes a prior publication via SIP PUBLISH with SIP-If-Match and Expires: 0 (RFC 3903 §4.1). The
    /// <paramref name="etag"/> is the SIP-ETag from a prior <see cref="PublishAsync"/>/refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to remove.</param>
    /// <param name="ct">Cancels the remove.</param>
    /// <returns>A task that completes when the peer answers 2xx; it faults on a non-2xx or no response.</returns>
    public async Task RemovePublicationAsync(string eventType, string etag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        await _channel.PublishAsync(eventType, string.Empty, "text/plain", expiresSeconds: 0, ifMatch: etag, ct).ConfigureAwait(false);
    }

    public Task UnregisterAsync(CancellationToken ct = default)
    {
        // Nothing was ever bound on a registration-free trunk, so there is nothing to remove: sending
        // REGISTER Expires:0 would put a request on the wire that the peer never expected and that
        // refers to a binding that does not exist. Just leave the operational state.
        if (!Account.Register)
        {
            TransitionTo(LineState.Unregistered);
            return Task.CompletedTask;
        }

        // Real de-registration: stop the refresh loop AND await the REGISTER Expires:0 round-trip
        // (RFC 3261 §10.2.2), so the returned task reflects the binding removal (HARD-E1) rather than
        // completing before the de-register is even sent.
        return _channel.StopRegistrationAsync(ct);
    }

    // ── Inbound ───────────────────────────────────────────────────────────────
    private void HandleInbound(ICallChannel channel, string remoteParty)
    {
        // Reserve the per-line slot atomically so concurrent inbound INVITEs can't all overshoot the cap.
        if (!TryReserveCallSlot())
        {
            _logger.LogWarning("Inbound call rejected: max calls reached on [{User}]", Account.Username);
            _ = ObserveHangupAsync(channel.HangupAsync(), "inbound rejection (max calls)");
            return;
        }

        Call call;
        try
        {
            call = CreateCall(CallId.New(), CallDirection.Inbound, remoteParty, channel);
        }
        catch
        {
            // Setup faulted before a Call took ownership of the reservation — release so the rejected
            // inbound never leaks per-line capacity (mirror of the outbound pre-creation failure path).
            ReleaseCallSlot();
            throw;
        }

        // Register (which subscribes the CallManager's aggregate StateChanged relay) BEFORE the first
        // Idle→Ringing transition, so the aggregate CallManager.CallStateChanged stream observes that inbound
        // transition instead of missing it — Register presupposes no particular call state (#17.8). The direct
        // IncomingCall event still fires exactly once, after the call is both registered and Ringing.
        _callRegistry.Register(call);
        call.TransitionTo(CallState.Ringing);
        IncomingCall?.Invoke(this, new IncomingCallEventArgs(call));
    }

    // An out-of-dialog MESSAGE (RFC 3428) carries no call state — surface it directly to subscribers.
    private void HandleInboundMessage(SipInstantMessage message)
        => IncomingMessage?.Invoke(this, new IncomingMessageEventArgs(message));

    // ── Helpers ───────────────────────────────────────────────────────────────
    private Call CreateCall(CallId id, CallDirection dir, string remote, ICallChannel channel)
    {
        var call = new Call(id, dir, remote, channel, this, _loggerFactory.CreateLogger<Call>());

        // Release the per-line slot reserved by TryReserveCallSlot exactly once — when this call
        // terminates. The guard makes the decrement idempotent even if Terminated is observed more than
        // once, so the increment-then-rollback cap can never be under-counted (which would leak capacity).
        var released = 0;
        call.StateChanged += (_, e) =>
        {
            if (e.NewState == CallState.Terminated && Interlocked.Exchange(ref released, 1) == 0)
                Interlocked.Decrement(ref _activeLineCallCount);
        };

        // Notify the application orchestrator (e.g. CallMediaOrchestrator) so it can
        // subscribe to the channel's MediaParametersNegotiated event.
        _onCallCreated?.Invoke(call, channel);

        return call;
    }

    // Atomically reserves one per-line call slot shared by the inbound and outbound admission paths. Uses
    // increment-then-rollback so that under N concurrent dials/INVITEs at most _maxCalls reservations can
    // ever succeed — a stale read can no longer let several callers pass a single free slot. A non-positive
    // _maxCalls means unlimited: the counter is still kept accurate, but a reservation never fails. Every
    // successful reservation is balanced by exactly one ReleaseCallSlot or the CreateCall terminate-release.
    private bool TryReserveCallSlot()
    {
        var reserved = Interlocked.Increment(ref _activeLineCallCount);
        if (_maxCalls > 0 && reserved > _maxCalls)
        {
            Interlocked.Decrement(ref _activeLineCallCount);
            return false;
        }
        return true;
    }

    // Releases a slot reserved by TryReserveCallSlot when call setup faults before a Call takes ownership of
    // the reservation (the terminate-release is wired inside CreateCall). Only ever runs on that pre-creation
    // failure path, so it can never double-release with the call's own terminate-release.
    private void ReleaseCallSlot() => Interlocked.Decrement(ref _activeLineCallCount);

    // Aborts a still-pending outbound dial after caller cancellation: sends CANCEL through the call's
    // hangup (the channel maps a pending/ringing outbound INVITE to a SIP CANCEL, RFC 3261 §9.1) and
    // guarantees the call ends Terminated even if the CANCEL send faults. A CANCEL failure must not
    // vanish from this critical path — it is logged rather than silently dropped (HARD-E2), and the
    // local terminal transition still runs so the returned call is always in a terminal state.
    private async Task CancelPendingDialAsync(Call call)
    {
        try
        {
            await call.HangupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CANCEL for cancelled dial failed on line [{User}].", Account.Username);
            call.TransitionTo(CallState.Terminated);
        }
    }

    // Observes a fire-and-forget hangup started from a synchronous path (inbound rejection, dispose):
    // the task cannot be awaited there, but a failure must not vanish from a critical path — it is
    // logged rather than silently dropped (HARD-E2). The observer itself never faults.
    private async Task ObserveHangupAsync(Task hangup, string context)
    {
        try
        {
            await hangup.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hangup during {Context} failed on line [{User}].", context, Account.Username);
        }
    }

    private void TransitionTo(LineState next)
    {
        LineStateChangedEventArgs? args;
        lock (_sync)
        {
            if (_state == next) return;
            // Guard illegal transitions the same way Call.TransitionTo does: log and ignore rather than apply
            // an out-of-band state change (#17.13).
            if (!LineStateRules.CanTransition(_state, next))
            {
                _logger.LogDebug(
                    "Line [{User}]: ignored invalid transition {Old} → {New}",
                    Account.Username, _state, next);
                return;
            }

            args   = new LineStateChangedEventArgs(_state, next, this);
            _state = next;
        }
        _logger.LogDebug("Line [{User}]: {Old} → {New}", Account.Username, args.OldState, next);
        StateChanged?.Invoke(this, args);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }

        // Only hang up calls that belong to this line.
        foreach (var call in _callRegistry.Active.Where(c => ReferenceEquals(c.Line, this)))
            _ = ObserveHangupAsync(call.HangupAsync(), "line dispose");

        _channel.Dispose();
    }
}
