using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// F011 Slice 3c: an outbound call that reaches Ringing (early dialog) before the 200 OK must surface
/// via the line's <see cref="IPhoneLine.OutboundCallRinging"/> event — giving the caller a handle to
/// observe early media while <c>DialAsync</c> still awaits the answer. The event must fire exactly once
/// with the ringing call, and must not fire when the dial never rings.
/// </summary>
public sealed class PhoneLineOutboundRingingTests
{
    [Fact]
    public async Task OutboundCallRinging_fires_once_with_the_call_when_dial_reaches_ringing()
    {
        var callChannel = new StateDrivingCallChannel();
        // The line channel drives the bound call to Ringing while StartOutboundDialAsync runs,
        // mimicking a 180/183 early dialog arriving before the 200 OK completes the dial.
        var lineChannel = new RingingLineChannel(callChannel, driveTo: CallState.Ringing);
        var line = NewLine(lineChannel);

        var raised = new List<ICall>();
        line.OutboundCallRinging += (_, e) => raised.Add(e.Call);

        var call = await line.DialAsync("sip:bob@example.com");

        Assert.Single(raised);
        Assert.Same(call, raised[0]);
        Assert.Equal(CallState.Ringing, call.State);
    }

    [Fact]
    public async Task OutboundCallRinging_does_not_fire_when_the_dial_never_rings()
    {
        var callChannel = new StateDrivingCallChannel();
        // No transition driven: the dial completes without ever reaching Ringing.
        var lineChannel = new RingingLineChannel(callChannel, driveTo: null);
        var line = NewLine(lineChannel);

        var raised = 0;
        line.OutboundCallRinging += (_, _) => raised++;

        await line.DialAsync("sip:bob@example.com");

        Assert.Equal(0, raised);
    }

    private static PhoneLine NewLine(ILineChannel channel)
    {
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            channel,
            new NoopCallRegistry(),
            maxCalls: 0,
            NullLoggerFactory.Instance);
        // DialAsync requires the line to be Registered; StartRegistration wires TransitionTo, and the
        // fake channel reports Registered synchronously through it.
        line.StartRegistration();
        return line;
    }

    // Registration on this fake immediately reports Registered so DialAsync's guard passes.
    private sealed class RingingLineChannel(StateDrivingCallChannel callChannel, CallState? driveTo) : ILineChannel
    {
        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
            => onStateChange(LineState.Registered);

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ICallChannel PrepareOutboundChannel(DialOptions options) => callChannel;

        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct)
        {
            // Simulate the early dialog: drive the bound call to Ringing while the dial is still
            // awaiting the answer, exactly as a 180/183 would before the 200 OK.
            if (driveTo is { } state)
                callChannel.Drive(state);
            return Task.CompletedTask;
        }

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public void SetMessageHandler(Action<CalloraVoipSdk.Core.Domain.Messages.SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    // Captures the OnStateChange callback the Call aggregate binds, and lets the test drive transitions.
    private sealed class StateDrivingCallChannel : ICallChannel
    {
        private Action<CallState>? _onStateChange;

        public void Drive(CallState state) => _onStateChange?.Invoke(state);

        public void BindCallbacks(CallChannelCallbacks callbacks) => _onStateChange = callbacks.OnStateChange;

        public void Dispose() { }

        public Task AnswerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HangupAsync() => Task.CompletedTask;
        public Task HoldAsync() => Task.CompletedTask;
        public Task UnholdAsync() => Task.CompletedTask;
        public Task SendDtmfAsync(byte dtmfCode) => Task.CompletedTask;
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

#pragma warning disable CS0067 // no media negotiation exercised in these tests
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
    }

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }
}
