using CalloraVoipSdk.Core.Application.Convenience;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #103 (L4 convenience, non-Docker regression for the Docker-only Asterisk interop suite): an
/// outbound INVITE rejected with a SIP final response (486 Busy, 480 no-answer, 603 decline, …) must
/// surface its <see cref="ICall.TerminationReason"/> through <c>DialAndWaitUntilConnectedAsync</c>. The
/// bug was that a rejection was propagated as an exception out of <see cref="IPhoneLine.DialAsync"/>,
/// so the convenience layer lost the (already-terminated, reason-carrying) call and returned
/// <c>result.Call == null</c>.
///
/// This drives the REAL <see cref="PhoneLine"/> + <see cref="Call"/> + <see cref="SdkConvenienceOrchestrator"/>
/// wiring. The fake line channel reproduces production ordering: the session→channel state path drives the
/// bound call to <see cref="CallState.Terminated"/> WITH a <see cref="CallTerminationReason"/> (as
/// <see cref="Core.Infrastructure.Sip.Adapters.SipCoreCallChannel"/> does from the session's Reason)
/// BEFORE the dial method throws its final-response exception. The fix makes <c>DialAsync</c> return that
/// terminated call instead of rethrowing, so the convenience result carries the reason.
/// </summary>
public sealed class DialOutboundRejectionTerminationReasonTests
{
    [Fact]
    public async Task Busy_486_surfaces_as_Failed_with_Busy_Remote_reason()
    {
        var outcome = await DialAndWaitForRejectionAsync(
            new CallTerminationReason
            {
                SipStatusCode = 486,
                ReasonPhrase  = "Busy Here",
                Category      = CallTerminationReason.CategoryForSipStatus(486),
                TerminatedBy  = CallTerminatedBy.Remote,
            });

        Assert.Equal(CallConnectStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Call);
        var reason = outcome.Call!.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.Busy, reason!.Category);
        Assert.Equal(486, reason.SipStatusCode);
        Assert.Equal(CallTerminatedBy.Remote, reason.TerminatedBy);
    }

    [Fact]
    public async Task NoAnswer_480_surfaces_as_Failed_with_NoAnswer_Remote_reason()
    {
        var outcome = await DialAndWaitForRejectionAsync(
            new CallTerminationReason
            {
                SipStatusCode = 480,
                ReasonPhrase  = "Temporarily Unavailable",
                Category      = CallTerminationReason.CategoryForSipStatus(480),
                TerminatedBy  = CallTerminatedBy.Remote,
            });

        Assert.Equal(CallConnectStatus.Failed, outcome.Status);
        var reason = outcome.Call?.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.NoAnswer, reason!.Category);
        Assert.Equal(480, reason.SipStatusCode);
    }

    [Fact]
    public async Task Decline_603_surfaces_as_Failed_with_Rejected_Remote_reason()
    {
        var outcome = await DialAndWaitForRejectionAsync(
            new CallTerminationReason
            {
                SipStatusCode = 603,
                ReasonPhrase  = "Decline",
                Category      = CallTerminationReason.CategoryForSipStatus(603),
                TerminatedBy  = CallTerminatedBy.Remote,
            });

        Assert.Equal(CallConnectStatus.Failed, outcome.Status);
        var reason = outcome.Call?.TerminationReason;
        Assert.NotNull(reason);
        Assert.Equal(CallTerminationCategory.Rejected, reason!.Category);
        Assert.Equal(603, reason.SipStatusCode);
    }

    private static async Task<CallConnectOutcome> DialAndWaitForRejectionAsync(CallTerminationReason reason)
    {
        var callChannel = new StateDrivingCallChannel();
        var lineChannel = new RejectingLineChannel(callChannel, reason);
        var line = NewLine(lineChannel);
        using var orchestrator = Orchestrator();

        return await orchestrator.DialAndWaitUntilConnectedAsync(
            line,
            "sip:busy@example.com",
            dialOptions: null,
            TimeSpan.FromSeconds(5),
            hangupOnTimeout: false,
            hangupOnCancellation: false,
            CancellationToken.None);
    }

    private static SdkConvenienceOrchestrator Orchestrator() =>
        new(new PhoneLineManager(_ => throw new NotSupportedException("lines are not registered here")),
            new MediaManager(), new NoopAudioDevice(), NullLoggerFactory.Instance, videoDevice: null);

    private static PhoneLine NewLine(ILineChannel channel)
    {
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            channel,
            new NoopCallRegistry(),
            maxCalls: 0,
            NullLoggerFactory.Instance);
        line.StartRegistration();
        return line;
    }

    // Reproduces the production final-response rejection: while StartOutboundDialAsync runs, the session→
    // channel path drives the bound call to Terminated WITH the reason (as SipCoreCallChannel does from the
    // session's SIP Reason), then the dial method throws its final-response exception. The exception TYPE is
    // deliberately not SipFinalResponseException here: PhoneLine.DialAsync classifies on the call's state
    // (already Terminated → return the call), not on the exception type, so a representative throw exercises
    // the exact fixed path without constructing an internal SipResponseEnvelope.
    private sealed class RejectingLineChannel(StateDrivingCallChannel callChannel, CallTerminationReason reason)
        : ILineChannel
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
            callChannel.Drive(CallState.Terminated, reason);
            throw new InvalidOperationException(
                $"INVITE rejected with {reason.SipStatusCode} {reason.ReasonPhrase}.");
        }

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public void SetMessageHandler(Action<CalloraVoipSdk.Core.Domain.Messages.SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CalloraVoipSdk.Core.Domain.Publications.PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => Task.FromResult(new CalloraVoipSdk.Core.Domain.Publications.PublishResult(null, 0));
        public void Dispose() { }
    }

    // Captures the OnStateChange callback the Call aggregate binds, and lets the test drive transitions
    // with an optional termination reason (as the real channel notifier does on Terminated).
    private sealed class StateDrivingCallChannel : ICallChannel
    {
        private Action<CallState, CallTerminationReason?>? _onStateChange;

        public void Drive(CallState state, CallTerminationReason? reason = null) => _onStateChange?.Invoke(state, reason);

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

    private sealed class NoopAudioDevice : IAudioDevice
    {
        public string Name => "noop";
        public void Connect(IMediaReceiver receiver, IMediaSender sender, AudioConnectionParameters parameters) { }
        public void Disconnect() { }
    }
}
