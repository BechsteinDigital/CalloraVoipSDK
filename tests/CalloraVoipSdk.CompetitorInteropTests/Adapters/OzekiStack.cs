using System.Collections.Concurrent;
using System.Diagnostics;
using Ozeki.Media;
using Ozeki.VoIP;

namespace MiniCore.Compare.Interop.Adapters;

public sealed class OzekiStack : IComparisonStack
{
    private readonly ISoftPhone _softPhone =
        SoftPhoneFactory.CreateSoftPhone(20_000, 30_000, "MiniCoreCompare-Ozeki/1.0");
    private readonly ConcurrentDictionary<string, OzekiCall> _calls = new();

    private IPhoneLine? _line;
    private bool _disposed;

    public string Name => "Ozeki";

    public bool IsRegistered => !_disposed && _line?.RegState == RegState.RegistrationSucceeded;

    public int ActiveCallCount => _calls.Values.Count(call => call.IsConnected);

    public async Task RegisterAsync(SipTestAccount account, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_line is not null)
        {
            throw new InvalidOperationException("Ozeki adapter is already registered.");
        }

        var sipAccount = new SIPAccount(
            registrationRequired: true,
            displayName: account.Username,
            userName: account.Username,
            registerName: account.Username,
            registerPassword: account.Password,
            domainServerHost: account.Server,
            domainServerPort: account.Port,
            proxy: $"{account.Server}:{account.Port}");
        var line = _softPhone.CreatePhoneLine(sipAccount);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRegistrationStateChanged(object? sender, RegistrationStateChangedArgs args)
        {
            if (args.State == RegState.RegistrationSucceeded)
            {
                completion.TrySetResult();
            }
            else if (args.State == RegState.Error)
            {
                completion.TrySetException(
                    new InvalidOperationException($"Ozeki registration failed: {args}"));
            }
        }

        line.RegistrationStateChanged += OnRegistrationStateChanged;
        _line = line;
        try
        {
            _softPhone.RegisterPhoneLine(line);
            await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(20), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            _line = null;
            line.Dispose();
            throw;
        }
        finally
        {
            line.RegistrationStateChanged -= OnRegistrationStateChanged;
        }
    }

    public async Task<DialAttempt> DialAsync(
        string targetUri,
        TimeSpan connectTimeout,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = _line ?? throw new InvalidOperationException("Register before dialing.");
        var call = _softPhone.CreateCallObject(line, ExtractDialTarget(targetUri));
        var stopwatch = Stopwatch.StartNew();
        var observedStates = new ConcurrentQueue<string>();
        observedStates.Enqueue($"{stopwatch.ElapsedMilliseconds}ms {call.CallState}");
        var completion = new TaskCompletionSource<DialAttemptStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCallStateChanged(object? sender, CallStateChangedArgs args)
        {
            observedStates.Enqueue(
                $"{stopwatch.ElapsedMilliseconds}ms {args.State} ({args.Reason})");
            if (IsConnectedState(args.State))
            {
                completion.TrySetResult(DialAttemptStatus.Connected);
            }
            else if (IsTerminalState(args.State))
            {
                completion.TrySetResult(DialAttemptStatus.Failed);
            }
        }

        call.CallStateChanged += OnCallStateChanged;
        try
        {
            if (!call.Start())
            {
                return new DialAttempt(
                    DialAttemptStatus.Failed,
                    Detail: $"Ozeki Start returned false. States: {string.Join(" -> ", observedStates)}");
            }

            DialAttemptStatus status;
            try
            {
                status = await completion.Task
                    .WaitAsync(connectTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                call.HangUp();
                return new DialAttempt(
                    DialAttemptStatus.Timeout,
                    Detail: $"Ozeki states: {string.Join(" -> ", observedStates)}");
            }
            catch (OperationCanceledException)
            {
                call.HangUp();
                return new DialAttempt(
                    DialAttemptStatus.Canceled,
                    Detail: $"Ozeki dial was canceled. States: {string.Join(" -> ", observedStates)}");
            }

            if (status != DialAttemptStatus.Connected)
            {
                return new DialAttempt(
                    DialAttemptStatus.Failed,
                    Detail:
                        $"Ozeki call ended in {call.CallState}. " +
                        $"States: {string.Join(" -> ", observedStates)}");
            }

            return new DialAttempt(
                DialAttemptStatus.Connected,
                await TrackAsync(call).ConfigureAwait(false));
        }
        finally
        {
            call.CallStateChanged -= OnCallStateChanged;
        }
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

        var incoming = new TaskCompletionSource<IPhoneCall>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnIncomingCall(object? sender, VoIPEventArgs<IPhoneCall> args) =>
            incoming.TrySetResult(args.Item);
        _softPhone.IncomingCall += OnIncomingCall;

        try
        {
            var call = await incoming.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            var connected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnCallStateChanged(object? sender, CallStateChangedArgs args)
            {
                if (IsConnectedState(args.State))
                {
                    connected.TrySetResult();
                }
                else if (IsTerminalState(args.State))
                {
                    connected.TrySetException(
                        new InvalidOperationException(
                            $"Ozeki inbound call ended in {args.State}: {args.Reason}"));
                }
            }

            call.CallStateChanged += OnCallStateChanged;
            try
            {
                if (!call.Answer())
                {
                    throw new InvalidOperationException("Ozeki Answer returned false.");
                }

                if (!IsConnectedState(call.CallState))
                {
                    await connected.Task
                        .WaitAsync(TimeSpan.FromSeconds(10), ct)
                        .ConfigureAwait(false);
                }

                return await TrackAsync(call).ConfigureAwait(false);
            }
            catch
            {
                if (!IsTerminalState(call.CallState))
                {
                    call.HangUp();
                }

                throw;
            }
            finally
            {
                call.CallStateChanged -= OnCallStateChanged;
            }
        }
        finally
        {
            _softPhone.IncomingCall -= OnIncomingCall;
        }
    }

    public IComparisonBridge Bridge(IComparisonCall left, IComparisonCall right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (left is not OzekiCall leftCall || right is not OzekiCall rightCall)
        {
            throw new ArgumentException("Both calls must belong to the Ozeki adapter.");
        }

        return new OzekiBridge(leftCall, rightCall);
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

        if (_line is { } line)
        {
            var unregistered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRegistrationStateChanged(object? sender, RegistrationStateChangedArgs args)
            {
                if (args.State == RegState.NotRegistered)
                {
                    unregistered.TrySetResult();
                }
                else if (args.State == RegState.Error)
                {
                    unregistered.TrySetException(
                        new InvalidOperationException($"Ozeki unregister failed: {args}"));
                }
            }

            line.RegistrationStateChanged += OnRegistrationStateChanged;
            try
            {
                var waitForUnregister = line.RegState != RegState.NotRegistered;
                _softPhone.UnregisterPhoneLine(line);
                if (waitForUnregister)
                {
                    await unregistered.Task
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
            finally
            {
                line.RegistrationStateChanged -= OnRegistrationStateChanged;
                line.Dispose();
            }
        }

        try
        {
            _softPhone.Close();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        _line = null;
        _disposed = true;

        if (failures is not null)
        {
            throw new AggregateException("Ozeki adapter cleanup failed.", failures);
        }
    }

    private async Task<OzekiCall> TrackAsync(IPhoneCall call)
    {
        var tracked = new OzekiCall(call, () => _calls.TryRemove(call.CallID, out _));
        if (!_calls.TryAdd(call.CallID, tracked))
        {
            await tracked.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Ozeki call {call.CallID} was already tracked.");
        }

        return tracked;
    }

    private static string ExtractDialTarget(string targetUri)
    {
        var target = targetUri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            ? targetUri[4..]
            : targetUri;
        var at = target.IndexOf('@');
        return at >= 0 ? target[..at] : target;
    }

    private static bool IsConnectedState(CallState state) =>
        state is CallState.Answered
            or CallState.InCall
            or CallState.LocalHeld
            or CallState.RemoteHeld
            or CallState.InactiveHeld;

    private static bool IsTerminalState(CallState state) =>
        state is CallState.Completed
            or CallState.Rejected
            or CallState.Cancelled
            or CallState.Busy
            or CallState.Error
            or CallState.Forwarded;

    private sealed class OzekiCall : IComparisonCall
    {
        private readonly IPhoneCall _call;
        private readonly Action _onDisposed;
        private readonly PhoneCallAudioReceiver _receiver = new();
        private readonly PhoneCallAudioSender _sender = new();
        private readonly MediaConnector _connector = new();
        private readonly ConcurrentQueue<char> _dtmf = new();

        private long _receivedPackets;
        private int _disposed;

        public OzekiCall(IPhoneCall call, Action onDisposed)
        {
            _call = call;
            _onDisposed = onDisposed;
            _receiver.MediaDataSent += OnMediaDataSent;
            _call.DtmfReceived += OnDtmfReceived;
            _receiver.AttachToCall(call);
            _sender.AttachToCall(call);
        }

        public bool IsConnected => IsConnectedState(_call.CallState);

        public bool IsOnHold =>
            _call.CallState is CallState.LocalHeld or CallState.InactiveHeld;

        public long ReceivedPacketCount => Interlocked.Read(ref _receivedPackets);

        public string ReceivedDtmf => new(_dtmf.ToArray());

        public PhoneCallAudioReceiver Receiver => _receiver;

        public PhoneCallAudioSender Sender => _sender;

        public Task HoldAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _call.Hold();
            return Task.CompletedTask;
        }

        public Task UnholdAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _call.Unhold();
            return Task.CompletedTask;
        }

        public async Task PlayWavAsync(string path, CancellationToken ct = default)
        {
            using var player = new WaveStreamPlayback(path, repeat: false, cacheStream: true);
            var stopped = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStopped(object? sender, EventArgs args) => stopped.TrySetResult();

            player.Stopped += OnStopped;

            if (!_connector.Connect(player, _sender))
            {
                player.Stopped -= OnStopped;
                throw new InvalidOperationException(
                    "Ozeki could not connect WAV playback to the call.");
            }

            player.Start();

            try
            {
                await stopped.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                player.Stop();
                throw;
            }
            finally
            {
                player.Stopped -= OnStopped;
                _connector.Disconnect(player, _sender);
            }
        }

        public Task<IComparisonRecording> StartWavRecordingAsync(
            string outputDirectory,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, "ozeki.wav");
            var recorder = new WaveStreamRecorder(path);
            if (!_connector.Connect(_receiver, recorder))
            {
                recorder.Dispose();
                throw new InvalidOperationException("Ozeki could not connect the call to WAV recording.");
            }

            recorder.Start();
            IComparisonRecording result =
                new OzekiRecording(_connector, _receiver, recorder, path);
            return Task.FromResult(result);
        }

        public Task HangupAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsTerminalState(_call.CallState))
            {
                _call.HangUp();
            }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _call.DtmfReceived -= OnDtmfReceived;
            _receiver.MediaDataSent -= OnMediaDataSent;
            try
            {
                await HangupAsync().ConfigureAwait(false);
            }
            finally
            {
                _receiver.Detach();
                _sender.Detach();
                _connector.Dispose();
                _receiver.Dispose();
                _sender.Dispose();
                _onDisposed();
            }
        }

        private void OnMediaDataSent(object? sender, AudioData args) =>
            Interlocked.Increment(ref _receivedPackets);

        private void OnDtmfReceived(object? sender, VoIPEventArgs<DtmfInfo> args) =>
            _dtmf.Enqueue(ToDtmfCharacter(args.Item.Signal.Signal));

        private static char ToDtmfCharacter(int tone) => tone switch
        {
            >= 0 and <= 9 => (char)('0' + tone),
            10 => '*',
            11 => '#',
            12 => 'A',
            13 => 'B',
            14 => 'C',
            15 => 'D',
            _ => '?',
        };
    }

    private sealed class OzekiRecording(
        MediaConnector connector,
        PhoneCallAudioReceiver receiver,
        WaveStreamRecorder recorder,
        string path) : IComparisonRecording
    {
        private int _stopped;

        public IReadOnlyList<string> OutputFiles => [path];

        public Task StopAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return Task.CompletedTask;
            }

            recorder.Stop();
            connector.Disconnect(receiver, recorder);
            recorder.Dispose();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private sealed class OzekiBridge : IComparisonBridge
    {
        private readonly OzekiCall _left;
        private readonly OzekiCall _right;
        private readonly MediaConnector _connector = new();
        private int _disposed;

        public OzekiBridge(OzekiCall left, OzekiCall right)
        {
            _left = left;
            _right = right;
            if (!_connector.Connect(_left.Receiver, _right.Sender))
            {
                _connector.Dispose();
                throw new InvalidOperationException("Ozeki could not connect the left bridge leg.");
            }

            if (!_connector.Connect(_right.Receiver, _left.Sender))
            {
                _connector.Disconnect(_left.Receiver, _right.Sender);
                _connector.Dispose();
                throw new InvalidOperationException("Ozeki could not connect the right bridge leg.");
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            _connector.Disconnect(_left.Receiver, _right.Sender);
            _connector.Disconnect(_right.Receiver, _left.Sender);
            _connector.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
