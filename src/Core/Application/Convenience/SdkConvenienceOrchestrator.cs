using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Application.Ports.Video;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Application.Convenience;

/// <summary>
/// Application-level orchestration service for additive SDK convenience workflows.
/// Keeps existing low-level event-driven flows untouched while providing
/// timeout/cancellation-aware happy-path helpers.
/// </summary>
internal sealed class SdkConvenienceOrchestrator : IDisposable
{
    private readonly PhoneLineManager _lineManager;
    private readonly MediaManager _mediaManager;
    private readonly IAudioDevice _audioDevice;
    private readonly IVideoDevice? _videoDevice;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SdkConvenienceOrchestrator> _logger;
    private readonly ConcurrentDictionary<CallId, DefaultAudioCallAttachment> _defaultAudioAttachments = new();
    private readonly ConcurrentDictionary<CallId, DefaultVideoCallAttachment> _defaultVideoAttachments = new();
    private int _disposed;

    /// <summary>
    /// Creates one convenience orchestrator bound to the SDK runtime instance.
    /// </summary>
    /// <param name="videoDevice">
    /// Optional application-supplied video codec device. <see langword="null"/> when no codec package is
    /// registered — <see cref="AttachDefaultVideoAsync"/> then fails closed. The SDK core ships no codec.
    /// </param>
    internal SdkConvenienceOrchestrator(
        PhoneLineManager lineManager,
        MediaManager mediaManager,
        IAudioDevice audioDevice,
        ILoggerFactory loggerFactory,
        IVideoDevice? videoDevice = null)
    {
        _lineManager = lineManager ?? throw new ArgumentNullException(nameof(lineManager));
        _mediaManager = mediaManager ?? throw new ArgumentNullException(nameof(mediaManager));
        _audioDevice = audioDevice ?? throw new ArgumentNullException(nameof(audioDevice));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _videoDevice = videoDevice;
        _logger = loggerFactory.CreateLogger<SdkConvenienceOrchestrator>();
    }

    /// <summary>
    /// Registers a line and waits until registration is complete, canceled, timed out, or failed.
    /// </summary>
    internal async Task<LineConnectOutcome> RegisterAndWaitAsync(
        SipAccount account,
        TimeSpan timeout,
        bool failFastOnRegistrationFailed,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(account);
        ValidatePositiveTimeout(timeout, nameof(timeout));

        IPhoneLine line;
        try
        {
            line = _lineManager.Register(account);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Convenience connect failed while registering line for user [{User}] at {Server}.",
                account.Username,
                account.SipServer);
            return new LineConnectOutcome(LineConnectStatus.Failed, null, null, ex);
        }

        var waiter = new TaskCompletionSource<LineState>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LineStateChangedEventArgs> onStateChanged = (_, args) =>
        {
            if (ShouldCompleteConnectWait(args.NewState, failFastOnRegistrationFailed))
                waiter.TrySetResult(args.NewState);
        };

        // Capture the permanent-failure reason so a terminal LineState.Failed surfaces it as the
        // ConnectResult error instead of a null cause (F005b). SipLineChannel raises this before the
        // state transition, so it is set by the time the waiter completes.
        LineReconnectFailedEventArgs? failure = null;
        EventHandler<LineReconnectFailedEventArgs> onReconnectFailed = (_, args) => failure ??= args;

        line.StateChanged += onStateChanged;
        line.LineReconnectFailed += onReconnectFailed;
        try
        {
            if (ShouldCompleteConnectWait(line.State, failFastOnRegistrationFailed))
                waiter.TrySetResult(line.State);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var finalState = await waiter.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            return finalState switch
            {
                LineState.Registered => new LineConnectOutcome(LineConnectStatus.Registered, line, finalState, null),
                _ => new LineConnectOutcome(LineConnectStatus.Failed, line, finalState, RegistrationFailureError(failure)),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new LineConnectOutcome(LineConnectStatus.Canceled, line, line.State, null);
        }
        catch (OperationCanceledException)
        {
            return new LineConnectOutcome(LineConnectStatus.Timeout, line, line.State, null);
        }
        finally
        {
            line.LineReconnectFailed -= onReconnectFailed;
            line.StateChanged -= onStateChanged;
        }
    }

    /// <summary>
    /// Starts an outbound call and waits until it becomes connected, canceled, timed out, or failed.
    /// </summary>
    internal async Task<CallConnectOutcome> DialAndWaitUntilConnectedAsync(
        IPhoneLine line,
        string targetUri,
        DialOptions? dialOptions,
        TimeSpan connectTimeout,
        bool hangupOnTimeout,
        bool hangupOnCancellation,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(targetUri))
            throw new ArgumentException("Target URI is required.", nameof(targetUri));
        ValidatePositiveTimeout(connectTimeout, nameof(connectTimeout));

        // connectTimeout bounds the WHOLE dial-and-wait, including line.DialAsync and its INVITE
        // transaction — otherwise a peer that keeps ringing past the ring/transaction timeout blocks
        // DialAsync far beyond connectTimeout and the call surfaces as Failed instead of Timeout (F008).
        using var timeoutCts = new CancellationTokenSource(connectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        ICall? call = null;
        try
        {
            call = await line.DialAsync(targetUri, dialOptions, linkedCts.Token).ConfigureAwait(false);

            var waiter = new TaskCompletionSource<CallState>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CallStateChangedEventArgs> onStateChanged = (_, args) =>
            {
                if (ShouldCompleteDialWait(args.NewState))
                    waiter.TrySetResult(args.NewState);
            };

            call.StateChanged += onStateChanged;
            try
            {
                if (ShouldCompleteDialWait(call.State))
                    waiter.TrySetResult(call.State);

                var finalState = await waiter.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                return finalState switch
                {
                    CallState.Connected or CallState.OnHold =>
                        new CallConnectOutcome(CallConnectStatus.Connected, call, finalState, null),
                    _ => new CallConnectOutcome(CallConnectStatus.Failed, call, finalState, null),
                };
            }
            finally
            {
                call.StateChanged -= onStateChanged;
            }
        }
        // Caller cancellation wins, however it surfaced — an OperationCanceledException from the token, or
        // a SIP transaction-terminated exception raised when the auto-CANCEL aborted the INVITE (F009).
        catch (Exception) when (ct.IsCancellationRequested)
        {
            if (hangupOnCancellation && call is not null)
                await TryHangupAsync(call).ConfigureAwait(false);

            return new CallConnectOutcome(CallConnectStatus.Canceled, call, call?.State, null);
        }
        // connectTimeout elapsed (still ringing, or the transaction timed out) → Timeout, not Failed (F008).
        catch (Exception) when (timeoutCts.IsCancellationRequested)
        {
            if (hangupOnTimeout && call is not null)
                await TryHangupAsync(call).ConfigureAwait(false);

            return new CallConnectOutcome(CallConnectStatus.Timeout, call, call?.State, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Convenience dial failed. target={TargetUri} line={LineId}",
                targetUri,
                line.LineId);
            return new CallConnectOutcome(CallConnectStatus.Failed, call, call?.State, ex);
        }
    }

    /// <summary>
    /// Attaches SDK default audio (receiver, sender, and configured audio device) to the call.
    /// Existing convenience default-audio attachments on other calls are replaced.
    /// </summary>
    internal Task AttachDefaultAudioAsync(ICall call, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(call);
        ct.ThrowIfCancellationRequested();

        // Convenience flow is optimized for one active default audio route.
        ReplaceOtherDefaultAudioAttachments(call.CallId);

        var attachment = _defaultAudioAttachments.GetOrAdd(
            call.CallId,
            _ => new DefaultAudioCallAttachment(
                call,
                _mediaManager,
                _audioDevice,
                _loggerFactory,
                (_, disposedAttachment) => RemoveDefaultAudioAttachment(call.CallId, disposedAttachment)));

        attachment.EnsureStarted();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Detaches convenience default audio from a call if present.
    /// </summary>
    internal Task DetachDefaultAudioAsync(ICall call, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(call);
        ct.ThrowIfCancellationRequested();

        if (_defaultAudioAttachments.TryRemove(call.CallId, out var attachment))
            attachment.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Attaches SDK default video (receiver, sender, and the application-supplied video codec device) to
    /// the call. Existing convenience default-video attachments on other calls are replaced.
    /// </summary>
    /// <exception cref="InvalidOperationException">No video codec device is registered.</exception>
    internal Task AttachDefaultVideoAsync(ICall call, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(call);
        ct.ThrowIfCancellationRequested();

        if (_videoDevice is null)
            throw new InvalidOperationException(
                "No video codec device is registered. Supply one via VoipConfiguration or dependency injection " +
                "(IVideoDevice); the SDK core is transport-only and ships no codec.");

        // Convenience flow is optimized for one active default video route.
        ReplaceOtherDefaultVideoAttachments(call.CallId);

        var attachment = _defaultVideoAttachments.GetOrAdd(
            call.CallId,
            _ => new DefaultVideoCallAttachment(
                call,
                _mediaManager,
                _videoDevice,
                _loggerFactory,
                (_, disposedAttachment) => RemoveDefaultVideoAttachment(call.CallId, disposedAttachment)));

        attachment.EnsureStarted();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Detaches convenience default video from a call if present.
    /// </summary>
    internal Task DetachDefaultVideoAsync(ICall call, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(call);
        ct.ThrowIfCancellationRequested();

        if (_defaultVideoAttachments.TryRemove(call.CallId, out var attachment))
            attachment.Dispose();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var entry in _defaultAudioAttachments.Values)
        {
            try
            {
                entry.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed disposing default audio attachment during SDK shutdown.");
            }
        }

        _defaultAudioAttachments.Clear();

        foreach (var entry in _defaultVideoAttachments.Values)
        {
            try
            {
                entry.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed disposing default video attachment during SDK shutdown.");
            }
        }

        _defaultVideoAttachments.Clear();
    }

    private void ReplaceOtherDefaultVideoAttachments(CallId currentCallId)
    {
        foreach (var pair in _defaultVideoAttachments)
        {
            if (pair.Key == currentCallId)
                continue;

            if (_defaultVideoAttachments.TryRemove(pair.Key, out var previous))
                previous.Dispose();
        }
    }

    private void RemoveDefaultVideoAttachment(CallId callId, DefaultVideoCallAttachment attachment)
    {
        if (_defaultVideoAttachments.TryGetValue(callId, out var existing)
            && ReferenceEquals(existing, attachment))
        {
            _defaultVideoAttachments.TryRemove(callId, out _);
        }
    }

    private void ReplaceOtherDefaultAudioAttachments(CallId currentCallId)
    {
        foreach (var pair in _defaultAudioAttachments)
        {
            if (pair.Key == currentCallId)
                continue;

            if (_defaultAudioAttachments.TryRemove(pair.Key, out var previous))
                previous.Dispose();
        }
    }

    private void RemoveDefaultAudioAttachment(CallId callId, DefaultAudioCallAttachment attachment)
    {
        if (_defaultAudioAttachments.TryGetValue(callId, out var existing)
            && ReferenceEquals(existing, attachment))
        {
            _defaultAudioAttachments.TryRemove(callId, out _);
        }
    }

    private static bool ShouldCompleteConnectWait(LineState state, bool failFastOnRegistrationFailed) =>
        state == LineState.Registered
        || state == LineState.Unregistered
        // Terminal, non-retryable failure (e.g. permanent auth rejection): always a definitive connect
        // outcome, so complete the wait immediately instead of blocking until the timeout and then
        // mis-reporting Timeout. RegistrationFailed is the RETRYABLE variant, gated by fail-fast (F005).
        || state == LineState.Failed
        || (failFastOnRegistrationFailed && state == LineState.RegistrationFailed);

    // Turns a captured permanent-registration-failure reason into a ConnectResult error so a Failed
    // outcome carries a machine-readable cause instead of null (F005b).
    private static Exception? RegistrationFailureError(LineReconnectFailedEventArgs? failure) =>
        failure is null
            ? null
            : new InvalidOperationException(failure.Reason switch
            {
                ReregisterFailReason.AuthenticationFailed => "Line registration failed: the SIP server rejected the credentials (401/403).",
                ReregisterFailReason.MaxRetriesExceeded => "Line registration failed: the maximum number of reconnect attempts was exceeded.",
                _ => $"Line registration failed: {failure.Reason}.",
            });

    private static bool ShouldCompleteDialWait(CallState state) =>
        state is CallState.Connected or CallState.OnHold or CallState.Terminated;

    private async Task TryHangupAsync(ICall call)
    {
        try
        {
            await call.HangupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort hangup failed for call {CallId}.", call.CallId);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SdkConvenienceOrchestrator));
    }

    private static void ValidatePositiveTimeout(TimeSpan timeout, string paramName)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(paramName, "Timeout must be greater than zero.");
    }

}

/// <summary>
/// Internal result for line convenience connect flow.
/// </summary>
internal readonly record struct LineConnectOutcome(
    LineConnectStatus Status,
    IPhoneLine? Line,
    LineState? FinalState,
    Exception? Error);

/// <summary>
/// Internal status for line convenience connect flow.
/// </summary>
internal enum LineConnectStatus
{
    Registered,
    Timeout,
    Canceled,
    Failed
}

/// <summary>
/// Internal result for call convenience dial-and-wait flow.
/// </summary>
internal readonly record struct CallConnectOutcome(
    CallConnectStatus Status,
    ICall? Call,
    CallState? FinalState,
    Exception? Error);

/// <summary>
/// Internal status for call convenience dial-and-wait flow.
/// </summary>
internal enum CallConnectStatus
{
    Connected,
    Timeout,
    Canceled,
    Failed
}
