using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A media session is installed before it is started — deliberately, so a packet arriving the moment the
/// socket opens finds it. A start that fails must therefore roll the installation back (#165 P2-7): before
/// this, a failed start was a log line and nothing else, leaving an entry in the orchestrator that held an
/// RTP socket and RTCP loops which were never started, with only the eventual call teardown left to reap it.
/// </summary>
public sealed class CallMediaOrchestratorStartRollbackTests
{
    [Fact]
    public async Task A_session_whose_start_fails_is_disposed_and_removed()
    {
        var session = new FailingStartSession();
        var channel = new InertCallChannel();
        using var orchestrator = new CallMediaOrchestrator(
            new SingleSessionFactory(session), NullLoggerFactory.Instance, new RtcpPacketCodec());

        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, new FakePhoneLine(), NullLogger<Call>.Instance);
        orchestrator.AttachCall(call, channel);

        channel.RaiseMediaNegotiated(AudioParams()); // non-ICE: installs synchronously, starts fire-and-forget

        Assert.True(await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "the session must be disposed after its start failed");
    }

    [Fact]
    public async Task A_rolled_back_session_does_not_block_the_next_negotiation()
    {
        var first = new FailingStartSession();
        var second = new RecordingSession();
        var channel = new InertCallChannel();
        using var orchestrator = new CallMediaOrchestrator(
            new QueuedSessionFactory([first, second]), NullLoggerFactory.Instance, new RtcpPacketCodec());

        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, new FakePhoneLine(), NullLogger<Call>.Instance);
        orchestrator.AttachCall(call, channel);

        channel.RaiseMediaNegotiated(AudioParams());
        Assert.True(await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // A re-INVITE after the failure: the replacement must start, and must not be torn down as a
        // "displaced" entry by the rollback of the first one.
        channel.RaiseMediaNegotiated(AudioParams());

        Assert.True(await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(second.WasDisposed);
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

    private class RecordingSession : ICallMediaSession
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed => Disposed.Task.IsCompleted;

        public IVideoMediaStream? Video => null;

        public virtual Task StartAsync(CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            return Task.CompletedTask;
        }

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

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStartSession : RecordingSession
    {
        public override Task StartAsync(CancellationToken ct = default) =>
            Task.FromException(new InvalidOperationException("media transport refused to start"));
    }

    private sealed class SingleSessionFactory(ICallMediaSession session) : ICallMediaSessionFactory
    {
        public ICallMediaSession Create(CallMediaParameters parameters) => session;
    }

    private sealed class QueuedSessionFactory(IEnumerable<ICallMediaSession> sessions) : ICallMediaSessionFactory
    {
        private readonly Queue<ICallMediaSession> _sessions = new(sessions);

        public ICallMediaSession Create(CallMediaParameters parameters) => _sessions.Dequeue();
    }

    private sealed class InertCallChannel : ICallChannel
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
