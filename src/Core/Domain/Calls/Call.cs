using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Domain aggregate for one call lifecycle.
/// Owns signaling state transitions and translates transport callbacks into domain events.
/// </summary>
internal sealed class Call : ICall, IDisposable
{
    private readonly ICallChannel  _channel;
    private readonly ILogger<Call> _logger;
    private readonly object        _sync   = new();
    // Serialises the public state-changing actions against each other (#165 P2-4). Every one of them is
    // "check, await signaling, commit", and without this two of them interleave inside the await: both pass
    // their guard, both drive the channel, and both commit — the second one onto a state its caller never
    // saw. Held across the whole action, so the channel round-trip is part of the critical section.
    // NOT reentrant: a StateChanged handler must not block on another action of the same call, which the K3
    // event contract already forbids (handlers must neither block nor throw).
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    // volatile: allows lock-free reads in State property; writes are always under _sync.
    private volatile int           _stateInt = (int)CallState.Idle;
    private CallQualitySnapshot    _qualitySnapshot = CallQualitySnapshot.CreateEmpty(DateTimeOffset.UtcNow);
    private CallRtpStatistics?     _rtpStatistics;
    private CallIceSnapshot?       _iceSnapshot;
    private CallIceState           _iceConnectionState = CallIceState.Disabled;
    private long?                  _recommendedVideoBitrateBps;
    private NetworkQuality?        _videoNetworkQuality;
    private CallTerminationReason? _terminationReason;

    // The reason an SDK-initiated teardown wants published, parked before the BYE goes out because the
    // channel terminates the call from inside its own HangupAsync (#261). Guarded by _sync.
    private CallTerminationReason? _pendingLocalReason;
    private bool                   _disposed;

    /// <inheritdoc />
    public CallId        CallId      { get; }

    /// <inheritdoc />
    public CallDirection Direction   { get; }

    /// <inheritdoc />
    public string        RemoteParty { get; }

    /// <inheritdoc />
    public DateTimeOffset StartedAt  { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public IPhoneLine    Line        { get; }

    /// <inheritdoc />
    public CallState State => (CallState)_stateInt;

    /// <inheritdoc />
    public CallMediaParameters? MediaParameters { get; private set; }

    /// <inheritdoc />
    public CallTerminationReason? TerminationReason { get { lock (_sync) return _terminationReason; } }

    /// <inheritdoc />
    public CallQualitySnapshot QualitySnapshot { get { lock (_sync) return _qualitySnapshot; } }

    /// <inheritdoc />
    public CallRtpStatistics? RtpStatistics { get { lock (_sync) return _rtpStatistics; } }

    /// <inheritdoc />
    public CallIceSnapshot? IceSnapshot { get { lock (_sync) return _iceSnapshot; } }

    /// <inheritdoc />
    public string? RemoteAssertedIdentity => _channel.RemoteAssertedIdentity;

    /// <inheritdoc />
    public string? Diversion => _channel.Diversion;

    /// <inheritdoc />
    public string? RemoteDisplayName => _channel.RemoteDisplayName;

    /// <inheritdoc />
    public string? RemoteNumber => _channel.RemoteNumber;

    /// <inheritdoc />
    public string? LocalParty => _channel.LocalParty;

    /// <inheritdoc />
    public string? CalledNumber => _channel.CalledNumber;

    /// <inheritdoc />
    public bool IsMuted => _channel.IsOutgoingAudioMuted;

    /// <inheritdoc />
    public string? EarlyMediaSdp => _channel.EarlyMediaSdp;

    // ── Events ────────────────────────────────────────────────────────────────
    /// <inheritdoc />
    public event EventHandler<CallStateChangedEventArgs>?  StateChanged;

    /// <inheritdoc />
    public event EventHandler<HoldStateChangedEventArgs>?  HoldStateChanged;

    /// <inheritdoc />
    public event EventHandler<DtmfReceivedEventArgs>?      DtmfReceived;

    /// <inheritdoc />
    public event EventHandler<TransferRequestedEventArgs>? TransferRequested;

    /// <inheritdoc />
    public event EventHandler<CallQualitySnapshotChangedEventArgs>? QualitySnapshotChanged;

    /// <inheritdoc />
    public event EventHandler<CallMediaFlowChangedEventArgs>? MediaFlowChanged;

    /// <summary>
    /// Creates a call aggregate and wires transport callbacks.
    /// </summary>
    internal Call(
        CallId        id,
        CallDirection direction,
        string        remoteParty,
        ICallChannel  channel,
        IPhoneLine    line,
        ILogger<Call> logger)
    {
        CallId      = id;
        Direction   = direction;
        RemoteParty = remoteParty;
        _channel    = channel;
        Line        = line;
        _logger     = logger;

        _channel.BindCallbacks(new CallChannelCallbacks(
            OnStateChange:       TransitionTo,
            OnDtmf:              RaiseDtmf,
            OnRemoteHold:        HandleRemoteHoldChanged,
            OnTransferRequested: RaiseTransferRequested));
    }

    /// <summary>
    /// Accepts an inbound ringing call and moves it to Connected.
    /// </summary>
    public async Task AcceptAsync(CancellationToken ct = default)
    {
        if (Direction != CallDirection.Inbound)
            throw new InvalidOperationException("Only inbound calls can be accepted.");

        await _actionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GuardState(CallState.Ringing);
            await _channel.AnswerAsync(ct).ConfigureAwait(false);
            if (CommitTransition(CallState.Ringing, CallState.Connected) == CallTransitionOutcome.Overtaken)
                throw OvertakenDuring("Accept", CallState.Ringing);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Hangs up the call and transitions to Terminated.
    /// </summary>
    public Task HangupAsync(CancellationToken ct = default) => HangupAsync(reason: null, ct);

    /// <summary>
    /// Hangs up the call carrying an explicit termination reason, so a consumer can tell an SDK-initiated
    /// teardown (a supervision timeout, #261) apart from a peer BYE on
    /// <see cref="CallStateChangedEventArgs.TerminationReason"/>. A <see langword="null"/> reason is the
    /// public <see cref="HangupAsync(CancellationToken)"/> behaviour.
    /// </summary>
    internal async Task HangupAsync(CallTerminationReason? reason, CancellationToken ct = default)
    {
        if (State == CallState.Terminated) return;

        await _actionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked inside the gate: another action may have terminated the call while this one waited.
            if (State == CallState.Terminated) return;

            // Parked before the BYE: the channel reports Terminated from inside HangupAsync, so this is the
            // only point at which a specific reason can still reach the transition (#261).
            if (reason is not null)
                lock (_sync) _pendingLocalReason = reason;

            try
            {
                await _channel.HangupAsync().ConfigureAwait(false);
                // Unconditional: termination is valid from every state and is the one transition that may
                // overtake anything else. Idempotent by the check above and by TransitionTo itself.
                TransitionTo(CallState.Terminated, reason);
            }
            finally
            {
                // Never leave a stale reason parked for a later, unrelated teardown — a failed BYE must not
                // relabel the next termination.
                lock (_sync) _pendingLocalReason = null;
            }
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Places the active call on hold and emits a local hold event.
    /// </summary>
    public async Task HoldAsync(CancellationToken ct = default)
    {
        await _actionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GuardState(CallState.Connected);
            await _channel.HoldAsync().ConfigureAwait(false);
            // The call may have terminated while the re-INVITE was in flight — a remote BYE does not wait for
            // the gate. Committing anyway reported success to the caller and raised a hold event on a
            // terminated call (probe: hold_race_final_state=Terminated hold_events_after_termination=1).
            if (CommitTransition(CallState.Connected, CallState.OnHold) == CallTransitionOutcome.Overtaken)
                throw OvertakenDuring("Hold", CallState.Connected);

            RaiseHoldChanged(isOnHold: true, byRemote: false);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Takes the call off hold and emits a local unhold event.
    /// </summary>
    public async Task UnholdAsync(CancellationToken ct = default)
    {
        await _actionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GuardState(CallState.OnHold);
            await _channel.UnholdAsync().ConfigureAwait(false);
            if (CommitTransition(CallState.OnHold, CallState.Connected) == CallTransitionOutcome.Overtaken)
                throw OvertakenDuring("Unhold", CallState.OnHold);

            RaiseHoldChanged(isOnHold: false, byRemote: false);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <inheritdoc />
    public Task MuteAsync(bool muted, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Local send-path gate only — no SIP signalling, no state transition, valid in any live state.
        _channel.SetOutgoingAudioMuted(muted);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initiates a local ICE restart (RFC 8445 §9). The call stays <see cref="CallState.Connected"/> —
    /// the restart re-negotiates only the ICE transport; the channel rejects it when ICE was not
    /// negotiated on this call.
    /// </summary>
    public async Task RestartIceAsync(CancellationToken ct = default)
    {
        GuardState(CallState.Connected);

        await _channel.RestartIceAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one DTMF tone. Allowed once the (early or confirmed) dialog can carry media:
    /// <see cref="CallState.Ringing"/> (early dialog, e.g. IVR navigation or AI-outbound bots that
    /// send DTMF before the 200 OK), <see cref="CallState.Connected"/>, and <see cref="CallState.OnHold"/>.
    /// The channel routes it via RTP telephone-event when the DTMF send delegate is wired
    /// (F011 Slice 3b wires it already at Ringing), otherwise via SIP INFO.
    /// </summary>
    public Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default)
    {
        if (State is not (CallState.Ringing or CallState.Connected or CallState.OnHold))
            throw new InvalidOperationException($"Cannot send DTMF in state {State}.");
        return _channel.SendDtmfAsync(tone.Code);
    }

    /// <summary>
    /// Performs a blind transfer and terminates this call when successful.
    /// </summary>
    public async Task BlindTransferAsync(string targetUri, CancellationToken ct = default)
    {
        GuardState(CallState.Connected);
        TransitionTo(CallState.Transferring);
        bool ok;
        try
        {
            ok = await _channel.BlindTransferAsync(targetUri, TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // The transfer signaling failed, timed out, or was cancelled — restore the connected call instead of
            // leaving it wedged in Transferring, where hold/DTMF/retry are all blocked and only hangup escapes.
            TransitionTo(CallState.Connected);
            throw;
        }
        TransitionTo(ok ? CallState.Terminated : CallState.Connected);
    }

    /// <summary>
    /// Performs an attended transfer with a consultation call.
    /// </summary>
    public async Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default)
    {
        if (consultationCall is not Call target)
            throw new ArgumentException("Must be a Call from this SDK.", nameof(consultationCall));

        // Attended transfer requires the same Connected precondition as a blind transfer — without this guard an
        // invalid TransitionTo(Transferring) is silently ignored (e.g. from Ringing) yet the channel transfer runs.
        GuardState(CallState.Connected);
        TransitionTo(CallState.Transferring);
        bool ok;
        try
        {
            ok = await _channel.AttendedTransferAsync(target._channel, TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // See BlindTransferAsync: restore the connected call rather than wedging it in Transferring.
            TransitionTo(CallState.Connected);
            throw;
        }
        TransitionTo(ok ? CallState.Terminated : CallState.Connected);
        return ok;
    }

    /// <inheritdoc />
    public async Task<CallActionResult> RejectAsync(
        int statusCode = 486,
        string? reasonPhrase = null,
        CancellationToken ct = default)
    {
        if (Direction != CallDirection.Inbound)
            return CallActionResult.Failure(
                CallActionStatus.InvalidState,
                "Reject is only valid for inbound calls.");

        await _actionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked inside the gate, not before it: the state may have moved on while this call waited.
            if (State != CallState.Ringing)
                return CallActionResult.Failure(
                    CallActionStatus.InvalidState,
                    $"Reject requires Ringing state, current state is {State}.");

            await _channel.RejectAsync(statusCode, reasonPhrase, ct).ConfigureAwait(false);
            if (CommitTransition(CallState.Ringing, CallState.Terminated) == CallTransitionOutcome.Overtaken)
                return OvertakenResult("Reject", CallState.Ringing);

            var resolvedReason = string.IsNullOrWhiteSpace(reasonPhrase)
                ? $"Rejected with SIP status {statusCode}."
                : reasonPhrase;
            return CallActionResult.Success(resolvedReason, statusCode);
        }
        catch (Exception ex)
        {
            return HandleCallActionException("Reject", ex);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CallActionResult> RedirectAsync(
        IReadOnlyList<string> contactUris,
        int statusCode = 302,
        CancellationToken ct = default)
    {
        if (Direction != CallDirection.Inbound)
            return CallActionResult.Failure(
                CallActionStatus.InvalidState,
                "Redirect is only valid for inbound calls.");

        await _actionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != CallState.Ringing)
                return CallActionResult.Failure(
                    CallActionStatus.InvalidState,
                    $"Redirect requires Ringing state, current state is {State}.");

            await _channel.RedirectAsync(contactUris, statusCode, ct).ConfigureAwait(false);
            if (CommitTransition(CallState.Ringing, CallState.Terminated) == CallTransitionOutcome.Overtaken)
                return OvertakenResult("Redirect", CallState.Ringing);

            return CallActionResult.Success("Redirect sent.", statusCode);
        }
        catch (Exception ex)
        {
            return HandleCallActionException("Redirect", ex);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CallActionResult> SendInfoAsync(
        string contentType,
        string body,
        CancellationToken ct = default)
    {
        try
        {
            await _channel.SendInfoAsync(contentType, body, ct).ConfigureAwait(false);
            return CallActionResult.Success("INFO sent.");
        }
        catch (Exception ex)
        {
            return HandleCallActionException("SendInfo", ex);
        }
    }

    /// <inheritdoc />
    public async Task<CallActionResult> SendOptionsAsync(CancellationToken ct = default)
    {
        try
        {
            var accepted = await _channel.SendOptionsAsync(ct).ConfigureAwait(false);
            return accepted
                ? CallActionResult.Success("OPTIONS accepted.")
                : CallActionResult.Failure(CallActionStatus.Rejected, "OPTIONS rejected by remote endpoint.");
        }
        catch (Exception ex)
        {
            return HandleCallActionException("SendOptions", ex);
        }
    }

    /// <inheritdoc />
    public async Task<CallActionResult> SendSubscribeAsync(
        string eventType,
        int expiresSeconds = 300,
        string? acceptHeader = null,
        string? body = null,
        CancellationToken ct = default)
    {
        try
        {
            var accepted = await _channel.SendSubscribeAsync(
                    eventType,
                    expiresSeconds,
                    acceptHeader,
                    body,
                    ct)
                .ConfigureAwait(false);
            return accepted
                ? CallActionResult.Success("SUBSCRIBE accepted.")
                : CallActionResult.Failure(CallActionStatus.Rejected, "SUBSCRIBE rejected by remote endpoint.");
        }
        catch (Exception ex)
        {
            return HandleCallActionException("SendSubscribe", ex);
        }
    }

    /// <inheritdoc />
    public async Task<CallActionResult> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType = null,
        string? body = null,
        CancellationToken ct = default)
    {
        try
        {
            var accepted = await _channel.SendNotifyAsync(
                    eventType,
                    subscriptionState,
                    contentType,
                    body,
                    ct)
                .ConfigureAwait(false);
            return accepted
                ? CallActionResult.Success("NOTIFY accepted.")
                : CallActionResult.Failure(CallActionStatus.Rejected, "NOTIFY rejected by remote endpoint.");
        }
        catch (Exception ex)
        {
            return HandleCallActionException("SendNotify", ex);
        }
    }

    /// <summary>
    /// Applies a state transition if allowed by <see cref="CallStateRules"/>.
    /// The <see cref="StateChanged"/> handler is snapshotted inside the lock so that a
    /// concurrent subscribe/unsubscribe cannot cause a null-dereference or lost-wake-up.
    /// </summary>
    internal void TransitionTo(CallState next, CallTerminationReason? reason = null)
        => CommitTransition(expected: null, next, reason);

    /// <summary>
    /// Commits a transition only if the call is still in <paramref name="expected"/> (#165 P2-4), so an action
    /// that checked its precondition before awaiting the signaling round-trip cannot commit onto a state that
    /// arrived while it was in flight. <see langword="null"/> means unconditional — the signaling and remote
    /// paths, which are reporting what already happened rather than requesting it.
    /// </summary>
    /// <returns>What happened — see <see cref="CallTransitionOutcome"/>.</returns>
    private CallTransitionOutcome CommitTransition(
        CallState? expected, CallState next, CallTerminationReason? reason = null)
    {
        CallStateChangedEventArgs? args;
        CallState current;
        EventHandler<CallStateChangedEventArgs>? stateChangedSnapshot;
        lock (_sync)
        {
            current = (CallState)_stateInt;
            // Already where the action wanted to go — the signaling callback got there first, which is the
            // ordinary case for a peer that answers before AnswerAsync returns. That is success, not a race.
            if (current == next) return CallTransitionOutcome.AlreadyInTargetState;
            if (expected is { } required && current != required)
                return CallTransitionOutcome.Overtaken;
            if (current == CallState.Terminated) return CallTransitionOutcome.Overtaken;
            if (!CallStateRules.CanTransition(current, next))
            {
                _logger.LogDebug(
                    "Call {Id}: ignored invalid transition {Old} → {New}",
                    CallId, current, next);
                return CallTransitionOutcome.Overtaken;
            }

            // Publish the termination reason under the same lock and before StateChanged fires, so a
            // handler reading TerminationReason on the Terminated transition always sees it set (K3).
            // A parked local reason wins: an SDK-initiated teardown sends its BYE through the channel, and
            // the channel reports Terminated from inside that call with only the generic "we hung up"
            // reason it can derive from SIP (no status, no phrase). Passing the specific reason after the
            // channel call would arrive at an already-terminated call and be dropped (#261).
            var terminationReason = next == CallState.Terminated ? _pendingLocalReason ?? reason : null;
            if (next == CallState.Terminated)
            {
                _terminationReason = terminationReason;
                _pendingLocalReason = null;
            }

            args               = new CallStateChangedEventArgs(current, next, this, terminationReason);
            _stateInt          = (int)next;
            stateChangedSnapshot = StateChanged; // snapshot before releasing lock
        }
        _logger.LogDebug("Call {Id}: {Old} → {New}", CallId, args.OldState, next);

        // K3 says a handler must not throw — but one that does must not take the call's own teardown with it
        // (#165 P2-5). Disposing the channel is this aggregate's invariant, not a subscriber's business: before
        // this, a throwing StateChanged handler on the Terminated transition left the channel undisposed, so the
        // call was terminated on paper while its signaling channel stayed alive. The throw is isolated and
        // logged at Error rather than propagated, so one bad subscriber cannot tear down the signaling thread
        // either. (A throw still cuts the multicast short for the handlers behind it — isolating each
        // subscriber individually is a separate change, not this invariant.)
        try
        {
            stateChangedSnapshot?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Call {Id}: a StateChanged subscriber threw on the {New} transition.", CallId, next);
        }
        finally
        {
            if (next == CallState.Terminated) _channel.Dispose();
        }

        return CallTransitionOutcome.Committed;
    }

    // The call left the state an action had checked while that action was talking to the peer. The signaling
    // side of it did happen, but the state it was going to commit is no longer the one it saw, so the caller
    // is told rather than handed a success it cannot rely on.
    private InvalidOperationException OvertakenDuring(string action, CallState expected) =>
        new($"{action} could not complete: the call left {expected} (now {State}) while the request was in flight.");

    private CallActionResult OvertakenResult(string action, CallState expected) =>
        CallActionResult.Failure(
            CallActionStatus.InvalidState,
            $"{action} could not complete: the call left {expected} (now {State}) while the request was in flight.");

    /// <summary>
    /// Raises DTMF events from the transport layer as domain events.
    /// Handler is snapshotted before invocation to prevent null-reference races.
    /// </summary>
    internal void RaiseDtmf(byte code, int durationMs)
    {
        var handler = DtmfReceived;
        handler?.Invoke(this, new DtmfReceivedEventArgs(DtmfTone.FromCode(code), durationMs, this));
    }

    /// <summary>
    /// Handles remote hold/unhold indications from SIP signaling.
    /// </summary>
    internal void HandleRemoteHoldChanged(bool isOnHold)
    {
        // Only raise HoldStateChanged when the hold state actually flips. A remote hold indication that
        // arrives before Connected, or a duplicate hold/unhold that matches the current state, produces
        // no CallState transition here — and must not re-raise the event for a non-change.
        bool changed;
        if (isOnHold && State == CallState.Connected)
        {
            TransitionTo(CallState.OnHold);
            changed = true;
        }
        else if (!isOnHold && State == CallState.OnHold)
        {
            TransitionTo(CallState.Connected);
            changed = true;
        }
        else
        {
            changed = false;
        }

        if (changed)
            RaiseHoldChanged(isOnHold, byRemote: true);
    }

    /// <summary>
    /// Raises transfer requests and returns whether the request is accepted.
    /// Handler is snapshotted before invocation to prevent null-reference races.
    /// </summary>
    internal bool RaiseTransferRequested(string referTo, string referredBy, IReferSubscription subscription)
    {
        var args    = new TransferRequestedEventArgs(referTo, this, subscription);
        var handler = TransferRequested;
        handler?.Invoke(this, args);
        return args.Accept;
    }

    /// <summary>
    /// Registers an incoming audio frame listener for this call.
    /// </summary>
    internal void AddAudioFrameListener(Action<CallAudioFrame> onFrame) =>
        _channel.AddAudioFrameListener(onFrame);

    /// <summary>
    /// Removes a previously registered audio frame listener.
    /// </summary>
    internal void RemoveAudioFrameListener(Action<CallAudioFrame> onFrame) =>
        _channel.RemoveAudioFrameListener(onFrame);

    /// <summary>
    /// Sends one outbound audio frame through the call channel.
    /// </summary>
    internal Task SendAudioFrameAsync(CallAudioFrame frame, CancellationToken ct = default)
    {
        if (State is not (CallState.Connected or CallState.OnHold))
            throw new InvalidOperationException($"Call must be Connected or OnHold, is {State}.");

        return _channel.SendAudioFrameAsync(frame, ct);
    }

    /// <summary>
    /// Registers an incoming video frame listener for this call.
    /// </summary>
    internal void AddVideoFrameListener(Action<CallVideoFrame> onFrame) =>
        _channel.AddVideoFrameListener(onFrame);

    /// <summary>
    /// Removes a previously registered video frame listener.
    /// </summary>
    internal void RemoveVideoFrameListener(Action<CallVideoFrame> onFrame) =>
        _channel.RemoveVideoFrameListener(onFrame);

    /// <summary>
    /// Sends one outbound encoded video frame through the call channel.
    /// </summary>
    internal Task SendVideoFrameAsync(CallVideoFrame frame, CancellationToken ct = default)
    {
        if (State is not (CallState.Connected or CallState.OnHold))
            throw new InvalidOperationException($"Call must be Connected or OnHold, is {State}.");

        return _channel.SendVideoFrameAsync(frame, ct);
    }

    /// <summary>
    /// Ensures a specific call state before running a signaling action.
    /// </summary>
    private void GuardState(CallState required)
    {
        if (State != required)
            throw new InvalidOperationException($"Call must be {required}, is {State}.");
    }

    /// <summary>
    /// Maps signaling action exceptions to a unified public result.
    /// </summary>
    private CallActionResult HandleCallActionException(string actionName, Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                _logger.LogInformation(
                    exception,
                    "Call {Id}: {Action} canceled.",
                    CallId,
                    actionName);
                return CallActionResult.Failure(CallActionStatus.Canceled, $"{actionName} canceled.");

            case ArgumentException:
                _logger.LogWarning(
                    exception,
                    "Call {Id}: {Action} rejected invalid request.",
                    CallId,
                    actionName);
                return CallActionResult.Failure(CallActionStatus.InvalidRequest, exception.Message);

            case InvalidOperationException:
                _logger.LogWarning(
                    exception,
                    "Call {Id}: {Action} invalid for current state.",
                    CallId,
                    actionName);
                return CallActionResult.Failure(CallActionStatus.InvalidState, exception.Message);

            default:
                _logger.LogError(
                    exception,
                    "Call {Id}: {Action} failed.",
                    CallId,
                    actionName);
                return CallActionResult.Failure(CallActionStatus.Failed, exception.Message);
        }
    }

    /// <summary>
    /// Emits hold-state changed events with explicit local/remote origin.
    /// Handler is snapshotted before invocation to prevent null-reference races.
    /// </summary>
    private void RaiseHoldChanged(bool isOnHold, bool byRemote)
    {
        var handler = HoldStateChanged;
        handler?.Invoke(this, new HoldStateChangedEventArgs(isOnHold, byRemote, this));
    }

    /// <summary>
    /// Raised whenever <see cref="MediaParameters"/> is (re)assigned — including a mid-call re-INVITE codec
    /// change that keeps the call <see cref="CallState.Connected"/> and therefore raises no
    /// <see cref="StateChanged"/>. Lets application media wiring re-apply the negotiated codec on such a
    /// renegotiation. The handler is snapshotted inside the lock and invoked outside it, matching the other
    /// internal setters.
    /// </summary>
    internal event Action? MediaParametersChanged;

    /// <summary>
    /// Sets the negotiated media parameters once the SDP exchange is complete, and raises
    /// <see cref="MediaParametersChanged"/>. Called by the application media orchestrator, both on the
    /// initial answer and on a mid-call re-INVITE renegotiation.
    /// </summary>
    internal void SetMediaParameters(CallMediaParameters parameters)
    {
        Action? handler;
        lock (_sync)
        {
            MediaParameters = parameters;
            handler         = MediaParametersChanged; // snapshot before releasing lock
        }
        handler?.Invoke();
    }

    /// <summary>
    /// Updates the latest quality snapshot and emits <see cref="QualitySnapshotChanged"/>.
    /// The handler is snapshotted inside the lock to prevent races with concurrent
    /// subscribe/unsubscribe operations.
    /// Called by application media orchestration after each quality recomputation.
    /// </summary>
    internal void SetQualitySnapshot(CallQualitySnapshot snapshot)
    {
        EventHandler<CallQualitySnapshotChangedEventArgs>? snapshotChangedHandler;
        lock (_sync)
        {
            _qualitySnapshot       = snapshot;
            snapshotChangedHandler = QualitySnapshotChanged; // snapshot before releasing lock
        }
        snapshotChangedHandler?.Invoke(this, new CallQualitySnapshotChangedEventArgs(snapshot, this));
    }

    /// <summary>
    /// Reports a change in inbound media flow and emits <see cref="MediaFlowChanged"/> (#261, ADR-069).
    /// The handler is snapshotted inside the lock to prevent races with concurrent subscribe/unsubscribe (K3).
    /// Called by application media supervision on transitions only, never per metrics tick.
    /// </summary>
    internal void ReportMediaFlowChanged(bool inboundMediaFlowing, TimeSpan silenceDuration)
    {
        EventHandler<CallMediaFlowChangedEventArgs>? mediaFlowHandler;
        lock (_sync)
        {
            mediaFlowHandler = MediaFlowChanged; // snapshot before releasing lock
        }
        mediaFlowHandler?.Invoke(this, new CallMediaFlowChangedEventArgs(inboundMediaFlowing, silenceDuration, this));
    }

    /// <summary>
    /// Updates the latest raw RTP statistics for this leg. Called by application media
    /// orchestration alongside each quality recomputation.
    /// </summary>
    internal void SetRtpStatistics(CallRtpStatistics statistics)
    {
        lock (_sync) _rtpStatistics = statistics;
    }

    /// <summary>
    /// Sets the ICE connectivity snapshot for this leg once candidate-pair selection completes.
    /// Called by the application media orchestrator; only invoked for ICE-enabled legs.
    /// </summary>
    internal void SetIceSnapshot(CallIceSnapshot snapshot)
    {
        lock (_sync) _iceSnapshot = snapshot;
    }

    /// <inheritdoc />
    public CallIceState IceConnectionState { get { lock (_sync) return _iceConnectionState; } }

    /// <inheritdoc />
    public event EventHandler<CallIceConnectionStateChangedEventArgs>? IceConnectionStateChanged;

    /// <summary>
    /// Updates the running ICE transport state and raises <see cref="IceConnectionStateChanged"/> when it
    /// actually changes. Called by the application media orchestrator (Connected on pair selection,
    /// Disconnected on RFC 7675 consent loss). Idempotent: setting the same state is a no-op.
    /// </summary>
    internal void SetIceConnectionState(CallIceState newState)
    {
        EventHandler<CallIceConnectionStateChangedEventArgs>? handler;
        CallIceState previous;
        lock (_sync)
        {
            if (_iceConnectionState == newState)
                return;

            previous              = _iceConnectionState;
            _iceConnectionState   = newState;
            handler               = IceConnectionStateChanged; // snapshot before releasing lock
        }
        handler?.Invoke(this, new CallIceConnectionStateChangedEventArgs(previous, newState, this));
    }

    /// <summary>
    /// Latest SDK-recommended outbound video bitrate (bits per second), or <see langword="null"/> when
    /// transport-cc congestion control is inactive for this leg. Read by the public video sender.
    /// </summary>
    internal long? RecommendedVideoBitrateBps { get { lock (_sync) return _recommendedVideoBitrateBps; } }

    /// <summary>
    /// Latest coarse network quality for this leg's video, or <see langword="null"/> when congestion
    /// control is inactive.
    /// </summary>
    internal NetworkQuality? VideoNetworkQuality { get { lock (_sync) return _videoNetworkQuality; } }

    /// <summary>
    /// Raised when the video congestion recommendation changes. Snapshotted inside the lock to avoid
    /// races with concurrent subscribe/unsubscribe. Subscribed by the public video sender.
    /// </summary>
    internal event Action? VideoCongestionChanged;

    /// <summary>
    /// Updates the video congestion recommendation and emits <see cref="VideoCongestionChanged"/>.
    /// Called by the application media orchestrator on each transport-cc feedback report.
    /// </summary>
    internal void SetVideoCongestion(long? recommendedBitrateBps, NetworkQuality? quality)
    {
        Action? handler;
        lock (_sync)
        {
            _recommendedVideoBitrateBps = recommendedBitrateBps;
            _videoNetworkQuality        = quality;
            handler                     = VideoCongestionChanged; // snapshot before releasing lock
        }
        handler?.Invoke();
    }

    /// <summary>
    /// Raised when the peer requested a keyframe via RTCP PLI/FIR — the application's encoder
    /// (feeding the public video sender) should emit an intra frame next. Snapshotted inside the
    /// lock to avoid races with concurrent subscribe/unsubscribe. Subscribed by the public video sender.
    /// </summary>
    internal event Action? VideoKeyFrameRequested;

    /// <summary>
    /// Emits <see cref="VideoKeyFrameRequested"/>. Called by the application media orchestrator when
    /// the video stream surfaces an inbound keyframe request.
    /// </summary>
    internal void RaiseVideoKeyFrameRequested()
    {
        Action? handler;
        lock (_sync) handler = VideoKeyFrameRequested; // snapshot before releasing lock
        handler?.Invoke();
    }

    /// <summary>
    /// Disposes the call and hangs up if still active.
    /// </summary>
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        // The action gate is deliberately not disposed: an action may still be inside it, and its Release in
        // the finally would then throw ObjectDisposedException over whatever it was doing. SemaphoreSlim only
        // needs disposal for its AvailableWaitHandle, which this never touches. Actions arriving after
        // disposal pass the gate and are turned away by their own state checks.
        if (State != CallState.Terminated)
        {
            // Best-effort BYE on dispose, THEN dispose the channel — never both at once. Disposing the
            // channel synchronously right after a fire-and-forget hangup tore the transport down while
            // the BYE was still in flight, so the BYE could fail on an already-disposed channel and the
            // dialog was left dangling on the peer. Dispose is synchronous and cannot await, so sequence
            // the channel dispose after the hangup completes (bounded) on a detached task instead.
            _ = HangupThenDisposeChannelAsync();
        }
        else
        {
            _channel.Dispose();
        }
    }

    /// <summary>
    /// Sends a best-effort BYE and disposes the channel only once it has completed (or a bounded
    /// timeout elapses), so the BYE reaches the wire before the transport is torn down. The channel is
    /// always disposed in the <c>finally</c>, so a hung or failed hangup can never leak it.
    /// </summary>
    private async Task HangupThenDisposeChannelAsync()
    {
        try
        {
            await _channel.HangupAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Best-effort hangup (BYE) on dispose of call {CallId} failed.",
                CallId);
        }
        finally
        {
            _channel.Dispose();
        }
    }
}
