using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Consumer-callback dispatch with early-event buffering for <see cref="SipCoreCallChannel"/>. Session
/// events (state change, DTMF, remote hold, transfer request) can fire before the SDK consumer binds
/// its handlers via <see cref="Bind"/>; state and remote-hold events are buffered until then and
/// flushed on bind, so no early transition is lost. Extracted from the channel so this concern owns its
/// own handlers, buffers and lock instead of sharing the channel's callback lock.
/// </summary>
internal sealed class SipCallChannelNotifier
{
    private readonly object _sync = new();
    private readonly Queue<CallState> _stateBuffer = new();
    private readonly Queue<bool> _remoteHoldBuffer = new();
    private Action<CallState>? _onStateChange;
    private Action<byte, int>? _onDtmf;
    private Action<bool>? _onRemoteHold;
    private Func<string, string, bool>? _onTransfer;

    /// <summary>Binds the consumer callbacks and flushes any buffered state/remote-hold events.</summary>
    public void Bind(CallChannelCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);

        List<CallState> pendingStates;
        List<bool> pendingRemoteHold;
        lock (_sync)
        {
            _onStateChange = callbacks.OnStateChange;
            _onDtmf = callbacks.OnDtmf;
            _onRemoteHold = callbacks.OnRemoteHold;
            _onTransfer = callbacks.OnTransferRequested;

            pendingStates = _stateBuffer.ToList();
            pendingRemoteHold = _remoteHoldBuffer.ToList();
            _stateBuffer.Clear();
            _remoteHoldBuffer.Clear();
        }

        // Flush outside the lock so a re-entrant consumer callback cannot deadlock on it.
        foreach (var state in pendingStates)
            callbacks.OnStateChange(state);

        if (callbacks.OnRemoteHold is null) return;
        foreach (var isOnHold in pendingRemoteHold)
            callbacks.OnRemoteHold(isOnHold);
    }

    /// <summary>Dispatches a state change, or buffers it until a handler is bound.</summary>
    public void NotifyState(CallState state)
    {
        Action<CallState>? handler;
        lock (_sync)
        {
            if (_onStateChange is null) { _stateBuffer.Enqueue(state); return; }
            handler = _onStateChange;
        }
        handler(state);
    }

    /// <summary>Dispatches a remote-hold change, or buffers it until a handler is bound.</summary>
    public void NotifyRemoteHold(bool isOnHold)
    {
        Action<bool>? handler;
        lock (_sync)
        {
            if (_onRemoteHold is null) { _remoteHoldBuffer.Enqueue(isOnHold); return; }
            handler = _onRemoteHold;
        }
        handler(isOnHold);
    }

    /// <summary>Dispatches a received DTMF tone; not buffered (nothing to replay).</summary>
    public void NotifyDtmf(byte toneCode, int durationMs)
    {
        Action<byte, int>? handler;
        lock (_sync) handler = _onDtmf;
        handler?.Invoke(toneCode, durationMs);
    }

    /// <summary>Asks the consumer whether to accept a transfer request; <c>false</c> when unbound.</summary>
    public bool NotifyTransferRequested(string referTo, string referredBy)
    {
        Func<string, string, bool>? handler;
        lock (_sync) handler = _onTransfer;
        return handler?.Invoke(referTo, referredBy) ?? false;
    }

    /// <summary>Clears buffers and handlers on channel teardown.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _stateBuffer.Clear();
            _remoteHoldBuffer.Clear();
            _onStateChange = null;
            _onDtmf = null;
            _onRemoteHold = null;
            _onTransfer = null;
        }
    }
}
