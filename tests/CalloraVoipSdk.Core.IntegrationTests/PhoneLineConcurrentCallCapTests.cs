using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [App/Domain] #165 P1-1: the per-line concurrent-call cap must be enforced atomically. The old code read
/// the counter and incremented it in two separate steps, so N simultaneous inbound INVITEs (or dials) could
/// all pass a single free slot and overshoot <c>maxCalls</c>. The reservation is now an increment-then-rollback
/// admission shared by both paths, and every successful reservation is released exactly once — these tests pin
/// both the race bound and the no-leak-on-setup-failure guarantee.
/// </summary>
public sealed class PhoneLineConcurrentCallCapTests
{
    [Fact]
    public async Task Concurrent_inbound_invites_never_exceed_the_per_line_cap()
    {
        const int cap = 1;
        const int racers = 8;

        var registry = new CountingCallRegistry();
        var lineChannel = new InboundCapturingLineChannel();
        _ = new PhoneLine(
            new SipAccount { Username = "u", SipServer = "s" },
            lineChannel, registry, maxCalls: cap, NullLoggerFactory.Instance);

        using var gate = new Barrier(racers);
        var tasks = Enumerable.Range(0, racers).Select(_ => Task.Run(() =>
        {
            gate.SignalAndWait();
            lineChannel.RaiseInbound(new InertCallChannel(), "sip:caller@remote.invalid");
        })).ToArray();

        await Task.WhenAll(tasks);

        // Only cap admissions may register; the rest are rejected before CreateCall. Under the old TOCTOU
        // read all racers could register.
        Assert.Equal(cap, registry.RegisteredCount);
    }

    [Fact]
    public async Task A_dial_that_faults_during_setup_releases_the_reserved_slot()
    {
        // cap == 1: if the failed dial leaked its reservation, the second dial would be rejected as over-cap.
        var lineChannel = new ThrowOnceOutboundLineChannel();
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "s" },
            lineChannel, new NoopCallRegistry(), maxCalls: 1, NullLoggerFactory.Instance);
        line.StartRegistration(); // the fake reports Registered synchronously so DialAsync's guard passes

        await Assert.ThrowsAsync<InvalidOperationException>(() => line.DialAsync("sip:bob@example.com"));

        // The slot must have been released by the pre-creation failure path, so this dial is admitted.
        var call = await line.DialAsync("sip:bob@example.com");
        Assert.NotNull(call);
    }

    private sealed class CountingCallRegistry : ICallRegistry
    {
        private int _registered;
        public int RegisteredCount => Volatile.Read(ref _registered);
        public void Register(Call call) => Interlocked.Increment(ref _registered);
        public IReadOnlyCollection<ICall> Active => Array.Empty<ICall>();
    }

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }

    // Captures the inbound handler the PhoneLine binds, so the test can raise concurrent inbound INVITEs.
    private sealed class InboundCapturingLineChannel : ILineChannel
    {
        private Action<ICallChannel, string>? _onInbound;

        public void RaiseInbound(ICallChannel channel, string remoteParty) => _onInbound!(channel, remoteParty);

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) => _onInbound = onInbound;

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        { }

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();
        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) => throw new NotSupportedException();
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    // Reports Registered synchronously; the first outbound channel preparation faults, later ones succeed.
    private sealed class ThrowOnceOutboundLineChannel : ILineChannel
    {
        private int _prepareCalls;

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
            => onStateChange(LineState.Registered);

        public ICallChannel PrepareOutboundChannel(DialOptions options)
        {
            if (Interlocked.Increment(ref _prepareCalls) == 1)
                throw new InvalidOperationException("simulated outbound setup failure");
            return new InertCallChannel();
        }

        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) => Task.CompletedTask;

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => Task.FromResult(new PublishResult(null, 0));
        public void Dispose() { }
    }

    // Inert call channel: the admission/ringing/dialing paths under test never exercise its media surface.
    private sealed class InertCallChannel : ICallChannel
    {
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

#pragma warning disable CS0067 // no media negotiation exercised here
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
    }
}
