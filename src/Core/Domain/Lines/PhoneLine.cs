using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

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

    public event EventHandler<LineStateChangedEventArgs>?    StateChanged;
    public event EventHandler<IncomingCallEventArgs>?         IncomingCall;
    public event EventHandler<LineReconnectingEventArgs>?    LineReconnecting;
    public event EventHandler<LineReconnectFailedEventArgs>? LineReconnectFailed;

    internal PhoneLine(
        SipAccount                    account,
        ILineChannel                  channel,
        ICallRegistry                 callRegistry,
        int                           maxCalls,
        ILoggerFactory                loggerFactory,
        Action<ICall, ICallChannel>?  onCallCreated = null)
    {
        Account         = account;
        _channel        = channel;
        _callRegistry   = callRegistry;
        _maxCalls       = maxCalls;
        _loggerFactory  = loggerFactory;
        _onCallCreated  = onCallCreated;
        _logger         = loggerFactory.CreateLogger<PhoneLine>();

        _channel.SetInboundHandler(HandleInbound);
    }

    internal void StartRegistration() =>
        _channel.StartRegistration(
            TransitionTo,
            onReconnecting: attempt =>
            {
                var handlers = LineReconnecting;
                handlers?.Invoke(this, new LineReconnectingEventArgs(attempt, this));
            },
            onReconnectFailed: (reason, attemptCount) =>
            {
                var handlers = LineReconnectFailed;
                handlers?.Invoke(this, new LineReconnectFailedEventArgs(reason, attemptCount, this));
            });

    // ── IPhoneLine ────────────────────────────────────────────────────────────
    public async Task<ICall> DialAsync(
        string targetUri, DialOptions? options = null, CancellationToken ct = default)
    {
        options ??= DialOptions.Default;

        if (State != LineState.Registered)
            throw new InvalidOperationException($"Line [{Account.Username}] is not registered.");

        if (_maxCalls > 0 && Volatile.Read(ref _activeLineCallCount) >= _maxCalls)
            throw new InvalidOperationException(
                $"Max concurrent calls ({_maxCalls}) reached on line [{Account.Username}].");

        var channel = _channel.PrepareOutboundChannel(options);
        var call = CreateCall(CallId.New(), CallDirection.Outbound, targetUri, channel);
        _callRegistry.Register(call);
        call.TransitionTo(CallState.Dialing);

        try
        {
            await _channel.StartOutboundDialAsync(channel, targetUri, options, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbound dial to {Uri} failed on [{User}]", targetUri, Account.Username);
            call.TransitionTo(CallState.Terminated);
            throw;
        }

        return call;
    }

    public Task UnregisterAsync(CancellationToken ct = default)
    {
        _channel.StopRegistration();
        return Task.CompletedTask;
    }

    // ── Inbound ───────────────────────────────────────────────────────────────
    private void HandleInbound(ICallChannel channel, string remoteParty)
    {
        if (_maxCalls > 0 && Volatile.Read(ref _activeLineCallCount) >= _maxCalls)
        {
            _logger.LogWarning("Inbound call rejected: max calls reached on [{User}]", Account.Username);
            channel.HangupAsync().ConfigureAwait(false);
            return;
        }

        var call = CreateCall(CallId.New(), CallDirection.Inbound, remoteParty, channel);
        call.TransitionTo(CallState.Ringing);
        _callRegistry.Register(call);
        IncomingCall?.Invoke(this, new IncomingCallEventArgs(call));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private Call CreateCall(CallId id, CallDirection dir, string remote, ICallChannel channel)
    {
        Interlocked.Increment(ref _activeLineCallCount);
        var call = new Call(id, dir, remote, channel, this, _loggerFactory.CreateLogger<Call>());

        // Decrement the per-line counter when the call terminates.
        call.StateChanged += (_, e) =>
        {
            if (e.NewState == CallState.Terminated)
                Interlocked.Decrement(ref _activeLineCallCount);
        };

        // Notify the application orchestrator (e.g. CallMediaOrchestrator) so it can
        // subscribe to the channel's MediaParametersNegotiated event.
        _onCallCreated?.Invoke(call, channel);

        return call;
    }

    private void TransitionTo(LineState next)
    {
        LineStateChangedEventArgs? args;
        lock (_sync)
        {
            if (_state == next) return;
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
            call.HangupAsync().ConfigureAwait(false);

        _channel.Dispose();
    }
}
