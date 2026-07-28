using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace MiniCore.Compare.Interop.Adapters;

public sealed class SipSorceryStack : IComparisonStack
{
    private readonly SIPTransport _transport = new();
    private readonly ConcurrentDictionary<Guid, SipSorceryCall> _calls = new();

    private SIPRegistrationUserAgent? _registration;
    private SipTestAccount? _account;
    private bool _disposed;

    public SipSorceryStack()
    {
        _transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Any, 0));
    }

    public string Name => "SipSorcery";

    public bool IsRegistered => !_disposed && _registration?.IsRegistered == true;

    public int ActiveCallCount => _calls.Values.Count(call => call.IsConnected);

    public async Task RegisterAsync(SipTestAccount account, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registration is not null)
        {
            throw new InvalidOperationException("SipSorcery adapter is already registered.");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new SIPRegistrationUserAgent(
            _transport,
            account.Username,
            account.Password,
            $"{account.Server}:{account.Port}",
            expiry: account.RegistrationExpirySeconds,
            maxRegistrationAttemptTimeout: 10,
            registerFailureRetryInterval: 2);

        registration.RegistrationSuccessful += (_, _) => completion.TrySetResult();
        registration.RegistrationFailed += (_, _, error) =>
            completion.TrySetException(new InvalidOperationException($"SipSorcery registration failed: {error}"));
        registration.RegistrationTemporaryFailure += (_, _, error) =>
            completion.TrySetException(
                new InvalidOperationException($"SipSorcery registration temporarily failed: {error}"));

        _registration = registration;
        _account = account;
        registration.Start();

        try
        {
            await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(20), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            registration.Stop(sendZeroExpiryRegister: false);
            _registration = null;
            _account = null;
            throw;
        }
    }

    public async Task<DialAttempt> DialAsync(
        string targetUri,
        TimeSpan connectTimeout,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var account = _account ?? throw new InvalidOperationException("Register before dialing.");

        var userAgent = new SIPUserAgent(_transport, outboundProxy: null);
        var mediaEndpoint = new PcmuAudioEndpoint();
        var mediaSession = CreateMediaSession(mediaEndpoint);
        var timeoutSeconds = Math.Max(1, checked((int)Math.Ceiling(connectTimeout.TotalSeconds)));
        var startedAt = DateTimeOffset.UtcNow;
        ComparisonTerminationReason? terminationReason = null;

        void OnClientCallFailed(
            ISIPClientUserAgent userAgent,
            string error,
            SIPResponse response)
        {
            terminationReason = ComparisonTerminationReason.FromRemoteSipResponse(
                response?.StatusCode,
                response is null ? error : $"{response.ReasonPhrase}; {error}");
        }

        userAgent.ClientCallFailed += OnClientCallFailed;

        bool connected;
        Task<bool>? callTask = null;
        try
        {
            callTask = userAgent.Call(
                targetUri,
                account.Username,
                account.Password,
                mediaSession,
                timeoutSeconds);
            connected = await callTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            userAgent.ClientCallFailed -= OnClientCallFailed;
            userAgent.Cancel();
            if (callTask is not null)
            {
                try
                {
                    await callTask
                        .WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                }
                catch (Exception observationFailure)
                {
                    mediaSession.Close("Dial cancellation cleanup failed.");
                    userAgent.Dispose();
                    throw new InvalidOperationException(
                        "SipSorcery dial cancellation cleanup failed.",
                        observationFailure);
                }
            }

            mediaSession.Close("Dial cancelled.");
            userAgent.Dispose();
            return new DialAttempt(
                DialAttemptStatus.Canceled,
                Detail: "SipSorcery dial was canceled and its User-Agent was disposed.");
        }
        catch
        {
            userAgent.ClientCallFailed -= OnClientCallFailed;
            mediaSession.Close("Dial failed.");
            userAgent.Dispose();
            throw;
        }

        if (!connected)
        {
            userAgent.ClientCallFailed -= OnClientCallFailed;
            mediaSession.Close("Dial did not connect.");
            userAgent.Dispose();
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var status = elapsed >= connectTimeout - TimeSpan.FromSeconds(1)
                ? DialAttemptStatus.Timeout
                : DialAttemptStatus.Failed;
            return new DialAttempt(
                status,
                Detail: $"Call returned false after {elapsed.TotalSeconds:F1}s.",
                TerminationReason: terminationReason);
        }

        userAgent.ClientCallFailed -= OnClientCallFailed;
        return new DialAttempt(
            DialAttemptStatus.Connected,
            Track(userAgent, mediaSession, mediaEndpoint));
    }

    public async Task<IComparisonCall> WaitForIncomingAndAnswerAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_account is null)
        {
            throw new InvalidOperationException("Register before accepting inbound calls.");
        }

        var userAgent = new SIPUserAgent(_transport, outboundProxy: null);
        var incoming = new TaskCompletionSource<SIPRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(SIPUserAgent _, SIPRequest request) => incoming.TrySetResult(request);
        userAgent.OnIncomingCall += Handler;

        try
        {
            var request = await incoming.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            var serverAgent = userAgent.AcceptCall(request);
            var mediaEndpoint = new PcmuAudioEndpoint();
            var mediaSession = CreateMediaSession(mediaEndpoint);
            var answered = await userAgent
                .Answer(serverAgent, mediaSession, IPAddress.Any)
                .WaitAsync(ct)
                .ConfigureAwait(false);

            if (!answered)
            {
                mediaSession.Close("Inbound answer failed.");
                throw new InvalidOperationException("SipSorcery failed to answer the inbound call.");
            }

            return Track(userAgent, mediaSession, mediaEndpoint);
        }
        catch
        {
            userAgent.Dispose();
            throw;
        }
        finally
        {
            userAgent.OnIncomingCall -= Handler;
        }
    }

    public IComparisonBridge Bridge(IComparisonCall left, IComparisonCall right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (left is not SipSorceryCall leftCall || right is not SipSorceryCall rightCall)
        {
            throw new ArgumentException("Both calls must belong to the SipSorcery adapter.");
        }

        return new SipSorceryBridge(leftCall, rightCall);
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

        if (_registration is { } registration)
        {
            var removed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRemoved(SIPURI _, SIPResponse response) => removed.TrySetResult();
            registration.RegistrationRemoved += OnRemoved;
            try
            {
                var waitForRemoval = registration.IsRegistered;
                registration.Stop(sendZeroExpiryRegister: true);
                if (waitForRemoval)
                {
                    await removed.Task
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
                registration.RegistrationRemoved -= OnRemoved;
            }
        }

        try
        {
            _transport.Shutdown();
            _transport.Dispose();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        _registration = null;
        _account = null;
        _disposed = true;

        if (failures is not null)
        {
            throw new AggregateException("SipSorcery adapter cleanup failed.", failures);
        }
    }

    private static VoIPMediaSession CreateMediaSession(PcmuAudioEndpoint endpoint) =>
        new(
            new MediaEndPoints
            {
                AudioSource = endpoint,
                AudioSink = endpoint,
            },
            testPatternSource: null);

    private SipSorceryCall Track(
        SIPUserAgent userAgent,
        VoIPMediaSession mediaSession,
        PcmuAudioEndpoint endpoint)
    {
        var id = Guid.NewGuid();
        var call = new SipSorceryCall(
            userAgent,
            mediaSession,
            endpoint,
            () => _calls.TryRemove(id, out _));
        if (!_calls.TryAdd(id, call))
        {
            throw new InvalidOperationException($"Failed to track SipSorcery call {id}.");
        }

        return call;
    }

    private sealed class SipSorceryCall : IComparisonCall
    {
        private readonly SIPUserAgent _userAgent;
        private readonly VoIPMediaSession _mediaSession;
        private readonly Action _onEnded;
        private readonly ConcurrentQueue<char> _dtmf = new();

        private long _receivedPackets;
        private int _disposed;

        public SipSorceryCall(
            SIPUserAgent userAgent,
            VoIPMediaSession mediaSession,
            PcmuAudioEndpoint endpoint,
            Action onEnded)
        {
            _userAgent = userAgent;
            _mediaSession = mediaSession;
            Endpoint = endpoint;
            _onEnded = onEnded;

            Endpoint.FrameReceived += OnFrameReceived;
            _userAgent.OnDtmfTone += OnDtmfTone;
        }

        public bool IsConnected => _userAgent.IsCallActive;

        public bool IsOnHold => _userAgent.IsOnLocalHold;

        public long ReceivedPacketCount => Interlocked.Read(ref _receivedPackets);

        public string ReceivedDtmf => new(_dtmf.ToArray());

        public PcmuAudioEndpoint Endpoint { get; }

        public Task HoldAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _userAgent.PutOnHold();
            return Task.CompletedTask;
        }

        public Task UnholdAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _userAgent.TakeOffHold();
            return Task.CompletedTask;
        }

        public async Task PlayWavAsync(string path, CancellationToken ct = default)
        {
            var samples = await SipSorceryPcmuWaveCodec.ReadPcm16Async(path, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));

            for (var offset = 0; offset < samples.Length; offset += SipSorceryPcmuWaveCodec.SamplesPerFrame)
            {
                var frameLength = Math.Min(SipSorceryPcmuWaveCodec.SamplesPerFrame, samples.Length - offset);
                var encoded = new byte[SipSorceryPcmuWaveCodec.SamplesPerFrame];
                encoded.AsSpan().Fill(0xFF);
                for (var i = 0; i < frameLength; i++)
                {
                    encoded[i] = SipSorceryPcmuWaveCodec.EncodeMuLaw(samples[offset + i]);
                }

                Endpoint.SendPcmu(encoded);
                if (offset + frameLength < samples.Length)
                {
                    await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
                }
            }
        }

        public Task<IComparisonRecording> StartWavRecordingAsync(
            string outputDirectory,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IComparisonRecording recording = new SipSorceryRecording(Endpoint, outputDirectory);
            return Task.FromResult(recording);
        }

        public Task HangupAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_userAgent.IsCallActive)
            {
                _userAgent.Hangup();
            }

            _mediaSession.Close("Comparison call ended.");
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Endpoint.FrameReceived -= OnFrameReceived;
            _userAgent.OnDtmfTone -= OnDtmfTone;

            try
            {
                await HangupAsync().ConfigureAwait(false);
                _userAgent.Dispose();
            }
            finally
            {
                _onEnded();
            }
        }

        private void OnFrameReceived(ReadOnlyMemory<byte> payload) =>
            Interlocked.Increment(ref _receivedPackets);

        private void OnDtmfTone(byte tone, int duration) => _dtmf.Enqueue(ToDtmfCharacter(tone));

        private static char ToDtmfCharacter(byte tone) => tone switch
        {
            <= 9 => (char)('0' + tone),
            10 => '*',
            11 => '#',
            12 => 'A',
            13 => 'B',
            14 => 'C',
            15 => 'D',
            _ => '?',
        };
    }

    private sealed class SipSorceryRecording : IComparisonRecording
    {
        private readonly PcmuAudioEndpoint _endpoint;
        private readonly object _sync = new();
        private readonly List<short> _samples = [];
        private readonly string _path;
        private int _stopped;

        public SipSorceryRecording(PcmuAudioEndpoint endpoint, string outputDirectory)
        {
            _endpoint = endpoint;
            Directory.CreateDirectory(outputDirectory);
            _path = Path.Combine(outputDirectory, "sipsorcery.wav");
            _endpoint.FrameReceived += OnFrameReceived;
        }

        public IReadOnlyList<string> OutputFiles => [_path];

        public async Task StopAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            _endpoint.FrameReceived -= OnFrameReceived;
            short[] samples;
            lock (_sync)
            {
                samples = _samples.ToArray();
            }

            await SipSorceryPcmuWaveCodec.WritePcm16Async(_path, samples, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        private void OnFrameReceived(ReadOnlyMemory<byte> payload)
        {
            lock (_sync)
            {
                foreach (var sample in payload.Span)
                {
                    _samples.Add(SipSorceryPcmuWaveCodec.DecodeMuLaw(sample));
                }
            }
        }
    }

    private sealed class SipSorceryBridge : IComparisonBridge
    {
        private readonly SipSorceryCall _left;
        private readonly SipSorceryCall _right;
        private int _disposed;

        public SipSorceryBridge(SipSorceryCall left, SipSorceryCall right)
        {
            _left = left;
            _right = right;
            _left.Endpoint.FrameReceived += ForwardLeftToRight;
            _right.Endpoint.FrameReceived += ForwardRightToLeft;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            _left.Endpoint.FrameReceived -= ForwardLeftToRight;
            _right.Endpoint.FrameReceived -= ForwardRightToLeft;
            return ValueTask.CompletedTask;
        }

        private void ForwardLeftToRight(ReadOnlyMemory<byte> payload) =>
            _right.Endpoint.SendPcmu(payload.Span);

        private void ForwardRightToLeft(ReadOnlyMemory<byte> payload) =>
            _left.Endpoint.SendPcmu(payload.Span);
    }

    public sealed class PcmuAudioEndpoint : IAudioSource, IAudioSink
    {
        private static readonly AudioFormat Pcmu =
            new(AudioCodecsEnum.PCMU, 0, SipSorceryPcmuWaveCodec.SampleRate, 1, string.Empty);

        private List<AudioFormat> _formats = [Pcmu];
        private bool _sourcePaused;
        private bool _sinkPaused;
        private bool _closed;

        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;

        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady
        {
            add { }
            remove { }
        }

        public event RawAudioSampleDelegate? OnAudioSourceRawSample
        {
            add { }
            remove { }
        }

        public event SourceErrorDelegate? OnAudioSourceError
        {
            add { }
            remove { }
        }

        public event SourceErrorDelegate? OnAudioSinkError
        {
            add { }
            remove { }
        }

        public event Action<ReadOnlyMemory<byte>>? FrameReceived;

        public List<AudioFormat> GetAudioSourceFormats() => _formats.ToList();

        public List<AudioFormat> GetAudioSinkFormats() => _formats.ToList();

        public void RestrictFormats(Func<AudioFormat, bool> filter) =>
            _formats = _formats.Where(filter).ToList();

        public void SetAudioSourceFormat(AudioFormat audioFormat) => EnsurePcmu(audioFormat);

        public void SetAudioSinkFormat(AudioFormat audioFormat) => EnsurePcmu(audioFormat);

        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample is not null;

        public bool IsAudioSourcePaused() => _sourcePaused;

        public Task StartAudio()
        {
            _closed = false;
            _sourcePaused = false;
            return Task.CompletedTask;
        }

        public Task PauseAudio()
        {
            _sourcePaused = true;
            return Task.CompletedTask;
        }

        public Task ResumeAudio()
        {
            _sourcePaused = false;
            return Task.CompletedTask;
        }

        public Task CloseAudio()
        {
            _closed = true;
            return Task.CompletedTask;
        }

        public Task StartAudioSink()
        {
            _sinkPaused = false;
            return Task.CompletedTask;
        }

        public Task PauseAudioSink()
        {
            _sinkPaused = true;
            return Task.CompletedTask;
        }

        public Task ResumeAudioSink()
        {
            _sinkPaused = false;
            return Task.CompletedTask;
        }

        public Task CloseAudioSink()
        {
            _sinkPaused = true;
            return Task.CompletedTask;
        }

        public void ExternalAudioSourceRawSample(
            AudioSamplingRatesEnum samplingRate,
            uint durationMilliseconds,
            short[] sample)
        {
            throw new NotSupportedException("The comparison endpoint accepts encoded PCMU only.");
        }

        public void GotAudioRtp(
            IPEndPoint remoteEndPoint,
            uint ssrc,
            uint sequenceNumber,
            uint timestamp,
            int payloadID,
            bool marker,
            byte[] payload)
        {
            if (!_sinkPaused && !_closed)
            {
                FrameReceived?.Invoke(payload);
            }
        }

        public void GotEncodedMediaFrame(EncodedAudioFrame encodedAudioFrame)
        {
            if (!_sinkPaused && !_closed)
            {
                FrameReceived?.Invoke(encodedAudioFrame.EncodedAudio);
            }
        }

        public void SendPcmu(ReadOnlySpan<byte> payload)
        {
            if (_closed || _sourcePaused)
            {
                return;
            }

            OnAudioSourceEncodedSample?.Invoke(
                SipSorceryPcmuWaveCodec.SamplesPerFrame,
                payload.ToArray());
        }

        private static void EnsurePcmu(AudioFormat format)
        {
            if (format.Codec != AudioCodecsEnum.PCMU)
            {
                throw new NotSupportedException(
                    $"The comparison endpoint supports PCMU only, not {format.FormatName}.");
            }
        }
    }
}
