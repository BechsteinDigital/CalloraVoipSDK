using System.Collections.ObjectModel;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Media;

namespace CalloraVoipSdk.Core.Application.Media.Sessions;

/// <summary>
/// Default recording session implementation used by <see cref="MediaManager"/>.
/// </summary>
internal sealed class RecordingSession : IRecordingSession
{
    private const int QueueCapacity = 512;

    private readonly object _sync = new();
    private readonly IRecordingFrameSource _source;
    private readonly IAudioFileCodec _codec;
    private readonly AudioPayloadTranscodingPlan _transcodingPlan;
    private readonly RecordingOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<MediaFrame> _frames;
    private readonly List<string> _outputFiles = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerLoop;

    private IAudioFileWriter? _writer;
    private MediaSessionState _state;
    private int _partIndex;
    private bool _disposed;

    /// <summary>
    /// Creates and starts one recording session.
    /// </summary>
    public RecordingSession(
        IRecordingFrameSource source,
        IAudioFileCodec codec,
        AudioPayloadTranscodingPlan transcodingPlan,
        RecordingOptions options,
        ILogger logger,
        bool startPaused)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _transcodingPlan = transcodingPlan ?? throw new ArgumentNullException(nameof(transcodingPlan));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _state = startPaused ? MediaSessionState.Paused : MediaSessionState.Running;
        SessionId = Guid.NewGuid();
        Format = _options.Format;

        _frames = Channel.CreateBounded<MediaFrame>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _source.FrameReceived += OnSourceFrameReceived;
        _writerLoop = Task.Run(WriterLoopAsync);
    }

    /// <inheritdoc />
    public Guid SessionId { get; }

    /// <inheritdoc />
    public AudioFileFormat Format { get; }

    /// <inheritdoc />
    public MediaSessionState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> OutputFiles
    {
        get
        {
            lock (_sync)
                return new ReadOnlyCollection<string>(_outputFiles.ToList());
        }
    }

    /// <inheritdoc />
    public event EventHandler<MediaSessionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<MediaSessionErrorEventArgs>? Error;

    /// <summary>
    /// Starts frame acquisition on the source.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await _source.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TransitionTo(MediaSessionState.Faulted, "Failed to start recording source.");
            RaiseError("record-start", "Starting recording source failed.", ex);
            throw;
        }
    }

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        TransitionTo(MediaSessionState.Paused, "Recording paused.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        TransitionTo(MediaSessionState.Running, "Recording resumed.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();

        MediaSessionState stateSnapshot;
        lock (_sync)
        {
            stateSnapshot = _state;
            if (stateSnapshot == MediaSessionState.Stopped)
                return;
        }

        _source.FrameReceived -= OnSourceFrameReceived;
        _frames.Writer.TryComplete();

        try
        {
            await _writerLoop.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled the stop. The writer loop runs off _cts — not this ct — so it would
            // keep reading frames and writing through _writer while the finally below disposes that
            // writer, an unsynchronized race on a torn-down writer. Cancel the loop and await it to
            // completion first, so the writer is only disposed once the loop has stopped touching it.
            _cts.Cancel();
            await _writerLoop.ConfigureAwait(false);
        }
        finally
        {
            await _source.DisposeAsync().ConfigureAwait(false);
            await DisposeWriterAsync().ConfigureAwait(false);

            try
            {
                await EncryptOutputsIfRequestedAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TransitionTo(MediaSessionState.Faulted, "Recording encryption failed.");
                RaiseError("record-encrypt", "Encrypting recording output failed.", ex);
                _logger.LogError(ex, "Recording session {SessionId} encryption failed.", SessionId);
            }

            if (State != MediaSessionState.Faulted)
                TransitionTo(MediaSessionState.Stopped, "Recording stopped.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            _cts.Cancel();
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dispose observed an exception while stopping recording session {SessionId}.", SessionId);
        }
        finally
        {
            _disposed = true;
            _cts.Dispose();
        }
    }

    private void OnSourceFrameReceived(MediaFrame frame)
    {
        if (_disposed)
            return;

        if (State != MediaSessionState.Running)
            return;

        _frames.Writer.TryWrite(frame);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            while (await _frames.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_frames.Reader.TryRead(out var frame))
                {
                    if (State != MediaSessionState.Running)
                        continue;

                    var toFileFrame = _transcodingPlan.ToFileFrame(frame);
                    if (ShouldSkipSilence(toFileFrame))
                        continue;
                    await EnsureWriterAsync(_cts.Token).ConfigureAwait(false);
                    await _writer!.WriteFrameAsync(toFileFrame, _cts.Token).ConfigureAwait(false);

                    if (_options.RotateAfterBytes is { } rotateAfter &&
                        rotateAfter > 0 &&
                        _writer.BytesWritten >= rotateAfter)
                    {
                        await RotateWriterAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            TransitionTo(MediaSessionState.Faulted, "Recording failed.");
            RaiseError("record-write", "Writing recording data failed.", ex);
            _logger.LogError(ex, "Recording session {SessionId} failed.", SessionId);
        }
        finally
        {
            await DisposeWriterAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureWriterAsync(CancellationToken ct)
    {
        if (_writer is not null)
            return;

        var directory = _options.OutputDirectory;
        Directory.CreateDirectory(directory);

        var nextPath = RecordingFileNamingStrategy.BuildFilePath(
            _options,
            _source.SourceToken,
            _partIndex,
            DateTimeOffset.UtcNow);

        _writer = await _codec.CreateWriterAsync(nextPath, _transcodingPlan.CodecContext, ct).ConfigureAwait(false);
        lock (_sync)
            _outputFiles.Add(nextPath);
        _partIndex++;
    }

    private async ValueTask RotateWriterAsync()
    {
        await DisposeWriterAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposeWriterAsync()
    {
        var writer = _writer;
        if (writer is null)
            return;

        _writer = null;
        await writer.DisposeAsync().ConfigureAwait(false);
    }

    private void TransitionTo(MediaSessionState newState, string reason)
    {
        MediaSessionState oldState;
        lock (_sync)
        {
            oldState = _state;
            if (oldState == newState)
                return;

            if (oldState == MediaSessionState.Stopped || oldState == MediaSessionState.Faulted)
                return;

            _state = newState;
        }

        StateChanged?.Invoke(
            this,
            new MediaSessionStateChangedEventArgs(oldState, newState, reason, DateTimeOffset.UtcNow));
    }

    private void RaiseError(string operation, string message, Exception exception)
    {
        Error?.Invoke(this, new MediaSessionErrorEventArgs(operation, message, exception));
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RecordingSession));
    }

    private bool ShouldSkipSilence(MediaFrame frame)
    {
        if (!_options.SkipSilence)
            return false;

        if (frame.Payload.Length < 2 || (frame.Payload.Length & 1) != 0)
            return false;

        var threshold = Math.Abs((int)_options.SilenceThresholdPcm16);
        if (threshold <= 0)
            return false;

        var payload = frame.Payload.Span;
        for (var i = 0; i < payload.Length; i += 2)
        {
            var sample = (short)(payload[i] | (payload[i + 1] << 8));
            if (Math.Abs((int)sample) > threshold)
                return false;
        }

        return true;
    }

    private async Task EncryptOutputsIfRequestedAsync(CancellationToken ct)
    {
        var provider = _options.EncryptionProvider;
        if (provider is null)
            return;

        List<string> plaintextFiles;
        lock (_sync)
            plaintextFiles = _outputFiles.ToList();

        if (plaintextFiles.Count == 0)
            return;

        var extension = provider.OutputFileExtension.Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension))
            extension = "enc";

        var encryptedFiles = new List<string>(plaintextFiles.Count);
        foreach (var plainPath in plaintextFiles)
        {
            ct.ThrowIfCancellationRequested();

            var encryptedPath = $"{plainPath}.{extension}";
            await provider.EncryptFileAsync(plainPath, encryptedPath, ct).ConfigureAwait(false);
            encryptedFiles.Add(encryptedPath);

            if (_options.DeletePlaintextAfterEncryption && File.Exists(plainPath))
                File.Delete(plainPath);
        }

        lock (_sync)
        {
            _outputFiles.Clear();
            _outputFiles.AddRange(encryptedFiles);
        }
    }
}
