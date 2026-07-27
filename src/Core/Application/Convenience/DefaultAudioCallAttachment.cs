using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.Application.Convenience;

/// <summary>
/// Lifecycle wrapper for default call audio wiring.
/// </summary>
internal sealed class DefaultAudioCallAttachment : IDisposable
{
    private readonly ICall _call;
    // Concrete Call handle (same assembly) when available, so we can observe MediaParametersChanged — a mid-call
    // re-INVITE codec change updates MediaParameters while the call stays Connected and raises no StateChanged,
    // so the state-driven path alone would never re-apply the negotiated codec (#17.3). Null for non-Call ICall
    // implementations (e.g. test fakes), which simply do not get the renegotiation trigger.
    private readonly Call? _concreteCall;
    private readonly IAudioDevice _audioDevice;
    private readonly IMediaReceiver _receiver;
    private readonly IMediaSender _sender;
    private readonly ILogger<DefaultAudioCallAttachment> _logger;
    private readonly Action<CallId, DefaultAudioCallAttachment> _onDisposed;
    private readonly object _sync = new();

    private bool _started;
    private bool _connected;
    private bool _disposed;

    // The parameters the audio device was last connected with. Kept so a connect made with
    // AudioConnectionParameters.Default (because the call had no negotiated MediaParameters yet) can be
    // re-applied to the real, negotiated codec once it arrives — instead of staying on PCMU/8k forever (#17.3).
    private AudioConnectionParameters? _appliedParameters;

    internal DefaultAudioCallAttachment(
        ICall call,
        MediaManager mediaManager,
        IAudioDevice audioDevice,
        ILoggerFactory loggerFactory,
        Action<CallId, DefaultAudioCallAttachment> onDisposed)
    {
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _concreteCall = call as Call;
        _audioDevice = audioDevice ?? throw new ArgumentNullException(nameof(audioDevice));
        ArgumentNullException.ThrowIfNull(mediaManager);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _onDisposed = onDisposed ?? throw new ArgumentNullException(nameof(onDisposed));

        _receiver = mediaManager.CreateReceiver();
        _sender = mediaManager.CreateSender();
        _logger = loggerFactory.CreateLogger<DefaultAudioCallAttachment>();
    }

    internal void EnsureStarted()
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DefaultAudioCallAttachment));

            if (!_started)
            {
                _call.StateChanged += OnCallStateChanged;
                // Re-evaluate the negotiated codec on a mid-call renegotiation that raises no StateChanged.
                if (_concreteCall is not null)
                    _concreteCall.MediaParametersChanged += OnMediaParametersChanged;
                _started = true;
            }
        }

        ApplyState(_call.State, throwOnConnectFailure: true);
    }

    private void OnCallStateChanged(object? sender, CallStateChangedEventArgs args)
    {
        try
        {
            ApplyState(args.NewState, throwOnConnectFailure: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Default audio transition failed for call {CallId} on state {CallState}.",
                _call.CallId,
                args.NewState);
        }
    }

    // Fired when MediaParameters is (re)assigned without a state change — notably a mid-call re-INVITE codec
    // change. Re-evaluates the audio path against the current state so ConnectIfNeeded re-applies the negotiated
    // codec (#17.3). Runs the same path as OnCallStateChanged; a failure is logged, never swallowed silently.
    private void OnMediaParametersChanged()
    {
        try
        {
            ApplyState(_call.State, throwOnConnectFailure: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Default audio renegotiation failed for call {CallId} on state {CallState}.",
                _call.CallId,
                _call.State);
        }
    }

    private void ApplyState(CallState state, bool throwOnConnectFailure)
    {
        if (state == CallState.Terminated)
        {
            Dispose();
            return;
        }

        if (state is not (CallState.Connected or CallState.OnHold))
            return;

        ConnectIfNeeded(throwOnConnectFailure);
    }

    private void ConnectIfNeeded(bool throwOnConnectFailure)
    {
        AudioConnectionParameters parameters;
        bool reapply;
        lock (_sync)
        {
            if (_disposed)
                return;

            parameters = _call.MediaParameters is { } mediaParameters
                ? AudioConnectionParameters.From(mediaParameters)
                : AudioConnectionParameters.Default;

            // First connect, or a codec change from a connect made with Default parameters (call had no
            // negotiated MediaParameters yet) to the real negotiated codec that has since arrived. Without the
            // re-apply the _connected guard would pin the device to PCMU/8k forever (#17.3). No re-apply when
            // the effective parameters are unchanged — a redundant device reconnect would just churn.
            if (_connected && SameEffectiveParameters(_appliedParameters, parameters))
                return;

            reapply = _connected;
        }

        try
        {
            // Re-apply path: drop the previous device wiring before re-opening on the new codec so the
            // backend never has two overlapping streams for the same call.
            if (reapply)
                TryDisconnectDevice();

            _receiver.AttachToCall(_call);
            _sender.AttachToCall(_call);
            _audioDevice.Connect(_receiver, _sender, parameters);

            lock (_sync)
            {
                if (!_disposed)
                {
                    _connected = true;
                    _appliedParameters = parameters;
                }
            }

            _logger.LogDebug(
                "Default audio {Action} for call {CallId} with PT={PayloadType} SR={SampleRate}.",
                reapply ? "re-applied" : "connected",
                _call.CallId,
                parameters.PayloadType,
                parameters.SampleRate);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _connected = false;
                    _appliedParameters = null;
                }
            }

            TryDetachMediaLegs();
            _logger.LogWarning(ex, "Default audio connect failed for call {CallId}.", _call.CallId);
            if (throwOnConnectFailure)
                throw;
        }
    }

    // Whether two effective device parameters are audio-equivalent: only the fields the backend opens a
    // stream / selects a codec from matter, so unchanged parameters skip a needless device reconnect (#17.3).
    private static bool SameEffectiveParameters(AudioConnectionParameters? a, AudioConnectionParameters b)
        => a is not null
           && a.PayloadType == b.PayloadType
           && a.ClockRate == b.ClockRate
           && a.SampleRate == b.SampleRate
           && string.Equals(a.CodecName, b.CodecName, StringComparison.Ordinal);

    private void TryDisconnectDevice()
    {
        try
        {
            _audioDevice.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Default audio disconnect before re-apply failed for call {CallId}.", _call.CallId);
        }
    }

    public void Dispose()
    {
        bool wasStarted;
        bool wasConnected;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            wasStarted = _started;
            wasConnected = _connected;
            _started = false;
            _connected = false;
        }

        if (wasStarted)
        {
            _call.StateChanged -= OnCallStateChanged;
            if (_concreteCall is not null)
                _concreteCall.MediaParametersChanged -= OnMediaParametersChanged;
        }

        if (wasConnected)
        {
            try
            {
                _audioDevice.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Default audio disconnect failed for call {CallId}.", _call.CallId);
            }
        }

        TryDetachMediaLegs();

        try
        {
            _receiver.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing media receiver failed for call {CallId}.", _call.CallId);
        }

        try
        {
            _sender.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing media sender failed for call {CallId}.", _call.CallId);
        }

        _onDisposed(_call.CallId, this);
    }

    private void TryDetachMediaLegs()
    {
        try
        {
            _receiver.Detach();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detaching media receiver failed for call {CallId}.", _call.CallId);
        }

        try
        {
            _sender.Detach();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detaching media sender failed for call {CallId}.", _call.CallId);
        }
    }
}
