using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Video;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.Application.Convenience;

/// <summary>
/// Lifecycle wrapper for default call video wiring. Mirrors
/// <see cref="DefaultAudioCallAttachment"/> for video: on connect it attaches a video receiver and
/// sender to the call and hands them to the application-supplied <see cref="IVideoDevice"/> (the codec
/// package), which owns capture/encode/decode/render. The SDK stays transport-only.
/// </summary>
internal sealed class DefaultVideoCallAttachment : IDisposable
{
    private readonly ICall _call;
    private readonly IVideoDevice _videoDevice;
    private readonly IVideoReceiver _receiver;
    private readonly IVideoSender _sender;
    private readonly ILogger<DefaultVideoCallAttachment> _logger;
    private readonly Action<CallId, DefaultVideoCallAttachment> _onDisposed;
    private readonly object _sync = new();

    private bool _started;
    private bool _connected;
    private bool _disposed;

    internal DefaultVideoCallAttachment(
        ICall call,
        MediaManager mediaManager,
        IVideoDevice videoDevice,
        ILoggerFactory loggerFactory,
        Action<CallId, DefaultVideoCallAttachment> onDisposed)
    {
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _videoDevice = videoDevice ?? throw new ArgumentNullException(nameof(videoDevice));
        ArgumentNullException.ThrowIfNull(mediaManager);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _onDisposed = onDisposed ?? throw new ArgumentNullException(nameof(onDisposed));

        _receiver = mediaManager.CreateVideoReceiver();
        _sender = mediaManager.CreateVideoSender();
        _logger = loggerFactory.CreateLogger<DefaultVideoCallAttachment>();
    }

    internal void EnsureStarted()
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DefaultVideoCallAttachment));

            if (!_started)
            {
                _call.StateChanged += OnCallStateChanged;
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
                "Default video transition failed for call {CallId} on state {CallState}.",
                _call.CallId,
                args.NewState);
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
        lock (_sync)
        {
            if (_disposed || _connected)
                return;
        }

        // Transport-only: the negotiated video codec is not yet surfaced publicly on the call, so the
        // device is handed the defaults for now (see follow-up: expose the negotiated video parameters).
        var parameters = VideoConnectionParameters.Default;

        try
        {
            _receiver.AttachToCall(_call);
            _sender.AttachToCall(_call);
            _videoDevice.Connect(_receiver, _sender, parameters);

            lock (_sync)
            {
                if (!_disposed)
                    _connected = true;
            }

            _logger.LogDebug(
                "Default video connected for call {CallId} with codec {Codec} PT={PayloadType}.",
                _call.CallId,
                parameters.CodecName,
                parameters.PayloadType);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (!_disposed)
                    _connected = false;
            }

            TryDetachMediaLegs();
            _logger.LogWarning(ex, "Default video connect failed for call {CallId}.", _call.CallId);
            if (throwOnConnectFailure)
                throw;
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
            _call.StateChanged -= OnCallStateChanged;

        if (wasConnected)
        {
            try
            {
                _videoDevice.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Default video disconnect failed for call {CallId}.", _call.CallId);
            }
        }

        TryDetachMediaLegs();

        try
        {
            _receiver.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing video receiver failed for call {CallId}.", _call.CallId);
        }

        try
        {
            _sender.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing video sender failed for call {CallId}.", _call.CallId);
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
            _logger.LogDebug(ex, "Detaching video receiver failed for call {CallId}.", _call.CallId);
        }

        try
        {
            _sender.Detach();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detaching video sender failed for call {CallId}.", _call.CallId);
        }
    }
}
