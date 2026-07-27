using System.Collections.Concurrent;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace MiniCore.Compare.Interop.Adapters;

public sealed class CalloraStack : IComparisonStack
{
    private readonly VoipClient _client = new(new VoipConfiguration
    {
        UserAgent = "MiniCoreCompare-Callora/1.0",
        SrtpPolicy = SrtpPolicy.Disabled,
        PreferredAudioCodecs = ["PCMU"],
    });
    private readonly ConcurrentDictionary<CallId, CalloraCall> _calls = new();

    private IPhoneLine? _line;
    private bool _disposed;

    public string Name => "Callora";

    public bool IsRegistered => !_disposed && _line is not null;

    public int ActiveCallCount => _calls.Count;

    public async Task RegisterAsync(SipTestAccount account, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = await _client.ConnectAsync(
                new SipAccount
                {
                    SipServer = account.Server,
                    Port = account.Port,
                    Username = account.Username,
                    Password = account.Password,
                    Transport = DomainSipTransport.Udp,
                },
                new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) },
                ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Line is null)
        {
            throw new InvalidOperationException(
                $"Callora registration failed: {result.Status}; {result.Error}");
        }

        _line = result.Line;
    }

    public async Task<DialAttempt> DialAsync(
        string targetUri,
        TimeSpan connectTimeout,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = _line ?? throw new InvalidOperationException("Register before dialing.");

        var result = await _client.DialAndWaitUntilConnectedAsync(
                line,
                targetUri,
                new DialWaitOptions { ConnectTimeout = connectTimeout },
                ct)
            .ConfigureAwait(false);

        if (result.IsSuccess && result.Call is not null)
        {
            return new DialAttempt(
                DialAttemptStatus.Connected,
                await TrackAsync(result.Call).ConfigureAwait(false));
        }

        return new DialAttempt(
            result.Status == DialStatus.Timeout ? DialAttemptStatus.Timeout : DialAttemptStatus.Failed,
            Detail: result.Status.ToString());
    }

    public async Task<IComparisonCall> WaitForIncomingAndAnswerAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_line is null)
        {
            throw new InvalidOperationException("Register before accepting inbound calls.");
        }

        var incoming = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, IncomingCallEventArgs args) => incoming.TrySetResult(args.Call);
        _client.IncomingCall += Handler;

        try
        {
            var call = await incoming.Task
                .WaitAsync(timeout, ct)
                .ConfigureAwait(false);
            await call.AcceptAsync(ct).ConfigureAwait(false);
            return await TrackAsync(call).ConfigureAwait(false);
        }
        finally
        {
            _client.IncomingCall -= Handler;
        }
    }

    public IComparisonBridge Bridge(IComparisonCall left, IComparisonCall right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (left is not CalloraCall leftCall || right is not CalloraCall rightCall)
        {
            throw new ArgumentException("Both calls must belong to the Callora adapter.");
        }

        return new CalloraBridge(leftCall, rightCall);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (var call in _calls.Values.ToArray())
        {
            try
            {
                await call.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (_line is not null)
        {
            try
            {
                await _line.UnregisterAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        _line = null;
        _disposed = true;

        if (failures is not null)
        {
            throw new AggregateException("Callora adapter cleanup failed.", failures);
        }
    }

    private async Task<CalloraCall> TrackAsync(ICall call)
    {
        var tracked = new CalloraCall(_client, call, () => _calls.TryRemove(call.CallId, out _));
        if (!_calls.TryAdd(call.CallId, tracked))
        {
            await tracked.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Call {call.CallId} was already tracked.");
        }

        return tracked;
    }

    private sealed class CalloraCall : IComparisonCall
    {
        private readonly VoipClient _owner;
        private readonly ICall _call;
        private readonly Action _onDisposed;
        private readonly IMediaReceiver _receiver;
        private readonly ConcurrentQueue<char> _dtmf = new();

        private long _receivedPackets;
        private int _disposed;

        public CalloraCall(VoipClient owner, ICall call, Action onDisposed)
        {
            _owner = owner;
            _call = call;
            _onDisposed = onDisposed;
            _receiver = owner.Media.CreateReceiver();
            _receiver.FrameReceived += OnFrameReceived;
            _receiver.AttachToCall(call);
            _call.DtmfReceived += OnDtmfReceived;
        }

        public bool IsConnected => _call.State is CallState.Connected or CallState.OnHold;

        public bool IsOnHold => _call.State == CallState.OnHold;

        public long ReceivedPacketCount => Interlocked.Read(ref _receivedPackets);

        public string ReceivedDtmf => new(_dtmf.ToArray());

        public ICall InnerCall => _call;

        public VoipClient Owner => _owner;

        public Task HoldAsync(CancellationToken ct = default) => _call.HoldAsync(ct);

        public Task UnholdAsync(CancellationToken ct = default) => _call.UnholdAsync(ct);

        public async Task PlayWavAsync(string path, CancellationToken ct = default)
        {
            var playback = await _owner.Media.StartCallPlaybackAsync(
                    _call,
                    new PlaybackRequest
                    {
                        FilePath = path,
                        Format = AudioFileFormat.Wav,
                        SampleRateHz = 8000,
                    },
                    ct)
                .ConfigureAwait(false);

            await using (playback.ConfigureAwait(false))
            {
                while (playback.State is MediaSessionState.Running or MediaSessionState.Paused)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }

                if (playback.State == MediaSessionState.Faulted)
                {
                    throw new InvalidOperationException("Callora WAV playback faulted.");
                }
            }
        }

        public async Task<IComparisonRecording> StartWavRecordingAsync(
            string outputDirectory,
            CancellationToken ct = default)
        {
            var recording = await _owner.Media.StartCallRecordingAsync(
                    _call,
                    new RecordingOptions
                    {
                        OutputDirectory = outputDirectory,
                        FileNamePrefix = "callora",
                        Format = AudioFileFormat.Wav,
                        IncludeUtcTimestamp = false,
                    },
                    ct)
                .ConfigureAwait(false);
            return new CalloraRecording(recording);
        }

        public Task HangupAsync(CancellationToken ct = default) => _call.HangupAsync(ct);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _call.DtmfReceived -= OnDtmfReceived;
            _receiver.FrameReceived -= OnFrameReceived;
            _receiver.Dispose();

            try
            {
                await _call.HangupAsync().ConfigureAwait(false);
            }
            finally
            {
                _onDisposed();
            }
        }

        private void OnFrameReceived(object? sender, MediaFrameReceivedEventArgs args) =>
            Interlocked.Increment(ref _receivedPackets);

        private void OnDtmfReceived(object? sender, DtmfReceivedEventArgs args) =>
            _dtmf.Enqueue(args.Tone.Symbol);
    }

    private sealed class CalloraRecording(IRecordingSession inner) : IComparisonRecording
    {
        public IReadOnlyList<string> OutputFiles => inner.OutputFiles;

        public Task StopAsync(CancellationToken ct = default) => inner.StopAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CalloraBridge : IComparisonBridge
    {
        private readonly IMediaReceiver _leftReceiver;
        private readonly IMediaSender _leftSender;
        private readonly IMediaReceiver _rightReceiver;
        private readonly IMediaSender _rightSender;
        private readonly IDisposable _connection;
        private int _disposed;

        public CalloraBridge(CalloraCall left, CalloraCall right)
        {
            _leftReceiver = left.Owner.Media.CreateReceiver();
            _leftSender = left.Owner.Media.CreateSender();
            _rightReceiver = right.Owner.Media.CreateReceiver();
            _rightSender = right.Owner.Media.CreateSender();

            _leftReceiver.AttachToCall(left.InnerCall);
            _leftSender.AttachToCall(left.InnerCall);
            _rightReceiver.AttachToCall(right.InnerCall);
            _rightSender.AttachToCall(right.InnerCall);

            _connection = left.Owner.Media.CreateConnector().CrossConnect(
                _leftReceiver,
                _leftSender,
                _rightReceiver,
                _rightSender);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            _connection.Dispose();
            _leftReceiver.Dispose();
            _leftSender.Dispose();
            _rightReceiver.Dispose();
            _rightSender.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
