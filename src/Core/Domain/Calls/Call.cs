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
    // volatile: allows lock-free reads in State property; writes are always under _sync.
    private volatile int           _stateInt = (int)CallState.Idle;
    private CallQualitySnapshot    _qualitySnapshot = CallQualitySnapshot.CreateEmpty(DateTimeOffset.UtcNow);
    private CallRtpStatistics?     _rtpStatistics;
    private CallIceSnapshot?       _iceSnapshot;
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
    public CallQualitySnapshot QualitySnapshot { get { lock (_sync) return _qualitySnapshot; } }

    /// <inheritdoc />
    public CallRtpStatistics? RtpStatistics { get { lock (_sync) return _rtpStatistics; } }

    /// <inheritdoc />
    public CallIceSnapshot? IceSnapshot { get { lock (_sync) return _iceSnapshot; } }

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

        GuardState(CallState.Ringing);
        await _channel.AnswerAsync(ct).ConfigureAwait(false);
        TransitionTo(CallState.Connected);
    }

    /// <summary>
    /// Hangs up the call and transitions to Terminated.
    /// </summary>
    public async Task HangupAsync(CancellationToken ct = default)
    {
        if (State == CallState.Terminated) return;

        await _channel.HangupAsync().ConfigureAwait(false);
        TransitionTo(CallState.Terminated);
    }

    /// <summary>
    /// Places the active call on hold and emits a local hold event.
    /// </summary>
    public async Task HoldAsync(CancellationToken ct = default)
    {
        GuardState(CallState.Connected);

        await _channel.HoldAsync().ConfigureAwait(false);
        TransitionTo(CallState.OnHold);
        RaiseHoldChanged(isOnHold: true, byRemote: false);
    }

    /// <summary>
    /// Takes the call off hold and emits a local unhold event.
    /// </summary>
    public async Task UnholdAsync(CancellationToken ct = default)
    {
        GuardState(CallState.OnHold);

        await _channel.UnholdAsync().ConfigureAwait(false);
        TransitionTo(CallState.Connected);
        RaiseHoldChanged(isOnHold: false, byRemote: false);
    }

    /// <summary>
    /// Sends one DTMF tone while connected.
    /// </summary>
    public Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default)
    {
        GuardState(CallState.Connected);
        return _channel.SendDtmfAsync(tone.Code);
    }

    /// <summary>
    /// Performs a blind transfer and terminates this call when successful.
    /// </summary>
    public async Task BlindTransferAsync(string targetUri, CancellationToken ct = default)
    {
        GuardState(CallState.Connected);
        TransitionTo(CallState.Transferring);
        var ok = await _channel.BlindTransferAsync(targetUri, TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        TransitionTo(ok ? CallState.Terminated : CallState.Connected);
    }

    /// <summary>
    /// Performs an attended transfer with a consultation call.
    /// </summary>
    public async Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default)
    {
        if (consultationCall is not Call target)
            throw new ArgumentException("Must be a Call from this SDK.", nameof(consultationCall));

        TransitionTo(CallState.Transferring);
        var ok = await _channel.AttendedTransferAsync(target._channel, TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
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

        if (State != CallState.Ringing)
            return CallActionResult.Failure(
                CallActionStatus.InvalidState,
                $"Reject requires Ringing state, current state is {State}.");

        try
        {
            await _channel.RejectAsync(statusCode, reasonPhrase, ct).ConfigureAwait(false);
            TransitionTo(CallState.Terminated);
            var resolvedReason = string.IsNullOrWhiteSpace(reasonPhrase)
                ? $"Rejected with SIP status {statusCode}."
                : reasonPhrase;
            return CallActionResult.Success(resolvedReason, statusCode);
        }
        catch (Exception ex)
        {
            return HandleCallActionException("Reject", ex);
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

        if (State != CallState.Ringing)
            return CallActionResult.Failure(
                CallActionStatus.InvalidState,
                $"Redirect requires Ringing state, current state is {State}.");

        try
        {
            await _channel.RedirectAsync(contactUris, statusCode, ct).ConfigureAwait(false);
            TransitionTo(CallState.Terminated);
            return CallActionResult.Success("Redirect sent.", statusCode);
        }
        catch (Exception ex)
        {
            return HandleCallActionException("Redirect", ex);
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
    internal void TransitionTo(CallState next)
    {
        CallStateChangedEventArgs? args;
        CallState current;
        EventHandler<CallStateChangedEventArgs>? stateChangedSnapshot;
        lock (_sync)
        {
            current = (CallState)_stateInt;
            if (current == next || current == CallState.Terminated) return;
            if (!CallStateRules.CanTransition(current, next))
            {
                _logger.LogDebug(
                    "Call {Id}: ignored invalid transition {Old} → {New}",
                    CallId, current, next);
                return;
            }

            args               = new CallStateChangedEventArgs(current, next, this);
            _stateInt          = (int)next;
            stateChangedSnapshot = StateChanged; // snapshot before releasing lock
        }
        _logger.LogDebug("Call {Id}: {Old} → {New}", CallId, args.OldState, next);
        stateChangedSnapshot?.Invoke(this, args);
        if (next == CallState.Terminated) _channel.Dispose();
    }

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
        if (isOnHold && State == CallState.Connected) TransitionTo(CallState.OnHold);
        if (!isOnHold && State == CallState.OnHold) TransitionTo(CallState.Connected);

        RaiseHoldChanged(isOnHold, byRemote: true);
    }

    /// <summary>
    /// Raises transfer requests and returns whether the request is accepted.
    /// Handler is snapshotted before invocation to prevent null-reference races.
    /// </summary>
    internal bool RaiseTransferRequested(string referTo, string referredBy)
    {
        var args    = new TransferRequestedEventArgs(referTo, this);
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
    /// Sets the negotiated media parameters once the SDP exchange is complete.
    /// Called by the application media orchestrator.
    /// </summary>
    internal void SetMediaParameters(CallMediaParameters parameters)
        => MediaParameters = parameters;

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

    /// <summary>
    /// Disposes the call and hangs up if still active.
    /// </summary>
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        if (State != CallState.Terminated)
        {
            // Best-effort BYE on dispose. Dispose is synchronous so we cannot await; observe the
            // task's fault via a continuation instead of letting a failed hangup vanish as an
            // unobserved task exception — a BYE failure during teardown must at least be logged.
            _ = _channel.HangupAsync().ContinueWith(
                t => _logger.LogWarning(
                    t.Exception,
                    "Best-effort hangup (BYE) on dispose of call {CallId} failed.",
                    CallId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        _channel.Dispose();
    }
}
