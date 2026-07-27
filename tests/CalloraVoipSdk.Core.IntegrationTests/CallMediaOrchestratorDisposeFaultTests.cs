using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #17.12 regression: the synchronous <see cref="CallMediaOrchestrator.Dispose"/> fires the active sessions'
/// <c>DisposeAsync</c> without awaiting them. A teardown fault on that fire-and-forget path must still be
/// observed and logged (a Warning), not silently discarded via <c>_ = …</c>.
/// </summary>
public sealed class CallMediaOrchestratorDisposeFaultTests
{
    [Fact]
    public async Task Sync_dispose_logs_a_warning_when_a_session_DisposeAsync_faults()
    {
        var logger = new CapturingLoggerFactory();
        var session = new ThrowingDisposeSession();
        var channel = new DisposeInertCallChannel();
        var orchestrator = new CallMediaOrchestrator(
            new SingleSessionFactory(session),
            logger,
            new RtcpPacketCodec());

        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, new FakePhoneLine(), NullLogger<Call>.Instance);
        orchestrator.AttachCall(call, channel);

        // Non-ICE negotiation installs the session synchronously.
        channel.RaiseMediaNegotiated(AudioParams());

        orchestrator.Dispose();

        // The fault is observed on a continuation of the fire-and-forget DisposeAsync — wait for the log.
        var logged = await logger.WarningLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(logged);
    }

    private static CallMediaParameters AudioParams() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        RtcpMux = true,
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    private sealed class ThrowingDisposeSession : ICallMediaSession
    {
        public IVideoMediaStream? Video => null;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken ct = default) => Task.CompletedTask;
        public void UpdateRoundTripTimeHint(TimeSpan roundTripTime) { }
        public CallMediaRuntimeMetrics GetRuntimeMetricsSnapshot() =>
            new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public CallMediaRtpSnapshot GetRtpSnapshot() =>
            new(DateTimeOffset.UtcNow, 0u, null, 0u, 0u, 0u, false, 0u, 0u, 0, 0, 0u, 0u, 0, 0, 0);
        public Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0067
        public event Action<CallAudioFrame>? FrameReceived;
        public event Action<byte, int>? DtmfReceived;
        public event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated;
        public event Action<IReadOnlyList<RtcpPacket>>? RtcpCompoundReceived;
        public event Action? MediaConsentLost;
        public event Action? MediaConnectivityDegraded;
        public event Action? MediaConnectivityRecovered;
#pragma warning restore CS0067

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("simulated media session teardown failure");
        }
    }

    private sealed class SingleSessionFactory(ICallMediaSession session) : ICallMediaSessionFactory
    {
        public ICallMediaSession Create(CallMediaParameters parameters) => session;
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public TaskCompletionSource<bool> WarningLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(WarningLogged);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(TaskCompletionSource<bool> warningLogged) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    warningLogged.TrySetResult(true);
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }

    private sealed class DisposeInertCallChannel : ICallChannel
    {
        public void RaiseMediaNegotiated(CallMediaParameters parameters) => MediaParametersNegotiated?.Invoke(this, parameters);

        public void BindCallbacks(CallChannelCallbacks callbacks) { }
        public void Dispose() { }
        public Task HangupAsync() => Task.CompletedTask;
        public Task SendDtmfAsync(byte dtmfCode) => Task.CompletedTask;
        public Task AnswerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HoldAsync() => Task.CompletedTask;
        public Task UnholdAsync() => Task.CompletedTask;
        public Task RejectAsync(int statusCode, string? reasonPhrase, CancellationToken ct) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode, CancellationToken ct) => Task.CompletedTask;
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> SendOptionsAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds, string? acceptHeader, string? body, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType, string? body, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> BlindTransferAsync(string targetUri, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AttendedTransferAsync(ICallChannel target, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task SendAudioFrameAsync(CallAudioFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendVideoFrameAsync(CallVideoFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public void AddAudioFrameListener(Action<CallAudioFrame> onFrame) { }
        public void RemoveAudioFrameListener(Action<CallAudioFrame> onFrame) { }
        public void AddVideoFrameListener(Action<CallVideoFrame> onFrame) { }
        public void RemoveVideoFrameListener(Action<CallVideoFrame> onFrame) { }
        public void DeliverInboundAudioFrame(CallAudioFrame frame) { }
        public void SetAudioSendDelegate(Func<CallAudioFrame, CancellationToken, Task>? sendDelegate) { }
        public void DeliverInboundVideoFrame(CallVideoFrame frame) { }
        public void SetVideoSendDelegate(Func<CallVideoFrame, CancellationToken, Task>? sendDelegate) { }

        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
    }
}
