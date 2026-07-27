using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L3 signaling coverage for issue #103: the intern-computed SIP termination reason surfaces
/// protocol-neutrally on the public call surface — for REMOTE terminations (486 Busy → Busy/Remote)
/// and LOCAL ones (hangup → Completed/Local) alike — and is already populated when the
/// <see cref="ICall.StateChanged"/> handler observes the <see cref="CallState.Terminated"/> transition.
/// The path exercised is the real <see cref="SipCoreCallChannel"/> + <see cref="Call"/> wiring, driven
/// by a controllable fake <see cref="ISipCallSession"/> that mirrors production ordering (its
/// <c>HangupAsync</c> raises the Terminated <c>StateChanged</c> synchronously, like the real session).
/// </summary>
public sealed class CallTerminationReasonTests
{
    private static (Call Call, SipCoreCallChannel Channel, ControllableSession Session)
        BuildRingingCall(SipDialogTerminationReason? terminationReason)
    {
        var channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        var call = new Call(
            CallId.New(),
            CallDirection.Inbound,
            "sip:remote@test.invalid",
            channel,
            new FakePhoneLine(),
            NullLogger<Call>.Instance);

        var session = new ControllableSession { LastTerminationReasonValue = terminationReason };
        channel.AttachSession(session); // session is Ringing → no media publish, just maps Ringing
        return (call, channel, session);
    }

    [Fact]
    public void Remote_busy_486_surfaces_as_Busy_Remote_and_is_readable_in_the_terminated_handler()
    {
        var (call, channel, session) = BuildRingingCall(
            new SipDialogTerminationReason("SIP", cause: 486, text: "Busy Here"));

        CallTerminationReason? seenInHandler = null;
        call.StateChanged += (_, e) =>
        {
            if (e.NewState == CallState.Terminated)
                seenInHandler = e.TerminationReason ?? call.TerminationReason;
        };

        // Remote party ends the call: the session raises Terminated without any local action first.
        session.RaiseStateChanged(SipDialogState.Terminated);

        Assert.NotNull(seenInHandler);
        Assert.Equal(CallTerminationCategory.Busy, seenInHandler!.Category);
        Assert.Equal(486, seenInHandler.SipStatusCode);
        Assert.Equal("Busy Here", seenInHandler.ReasonPhrase);
        Assert.Equal(CallTerminatedBy.Remote, seenInHandler.TerminatedBy);

        // Same reason is durably readable on the call surface afterwards.
        Assert.Same(seenInHandler, call.TerminationReason);

        channel.Dispose();
    }

    [Fact]
    public async Task Local_hangup_surfaces_as_TerminatedBy_Local()
    {
        // A normal BYE carries no SIP failure status → Completed; origin must be Local.
        var (call, channel, session) = BuildRingingCall(terminationReason: null);
        // Move to Connected so HangupAsync takes the established BYE path.
        session.CurrentState = SipDialogState.Established;
        session.RaiseStateChanged(SipDialogState.Established);

        CallTerminationReason? seenInHandler = null;
        call.StateChanged += (_, e) =>
        {
            if (e.NewState == CallState.Terminated)
                seenInHandler = e.TerminationReason ?? call.TerminationReason;
        };

        await call.HangupAsync();

        Assert.NotNull(seenInHandler);
        Assert.Equal(CallTerminatedBy.Local, seenInHandler!.TerminatedBy);
        Assert.Equal(CallTerminationCategory.Completed, seenInHandler.Category);
        Assert.Null(seenInHandler.SipStatusCode);
        Assert.Same(seenInHandler, call.TerminationReason);

        channel.Dispose();
    }

    [Fact]
    public void Remote_hangup_without_reason_surfaces_as_Completed_Remote()
    {
        var (call, channel, session) = BuildRingingCall(terminationReason: null);

        CallTerminationReason? seenInHandler = null;
        call.StateChanged += (_, e) =>
        {
            if (e.NewState == CallState.Terminated)
                seenInHandler = e.TerminationReason ?? call.TerminationReason;
        };

        session.RaiseStateChanged(SipDialogState.Terminated);

        Assert.NotNull(seenInHandler);
        Assert.Equal(CallTerminationCategory.Completed, seenInHandler!.Category);
        Assert.Null(seenInHandler.SipStatusCode);
        Assert.Equal(CallTerminatedBy.Remote, seenInHandler.TerminatedBy);

        channel.Dispose();
    }

    [Fact]
    public void Reason_is_null_before_termination()
    {
        var (call, channel, _) = BuildRingingCall(
            new SipDialogTerminationReason("SIP", cause: 486));

        Assert.Null(call.TerminationReason);

        channel.Dispose();
    }

    // Controllable session: mutable state + last-termination-reason, and a HangupAsync that mirrors the
    // production ordering by raising the Terminated StateChanged synchronously inside the call.
    private sealed class ControllableSession : ISipCallSession
    {
        public SipDialogState CurrentState { get; set; } = SipDialogState.Ringing;
        public SipDialogTerminationReason? LastTerminationReasonValue { get; set; }

        public void RaiseStateChanged(SipDialogState newState)
        {
            var old = CurrentState;
            CurrentState = newState;
            StateChanged?.Invoke(
                this,
                new SipDialogStateChangedEventArgs(
                    old,
                    newState,
                    newState == SipDialogState.Terminated ? LastTerminationReasonValue : null));
        }

        public string CallId => "termination-reason-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:remote@127.0.0.1";
        public SipDialogState State => CurrentState;
        public SipDialogTerminationReason? LastTerminationReason => LastTerminationReasonValue;
        public bool IsInbound => true;
        public string? RemoteAssertedIdentity => null;
        public string? Diversion => null;
        public string? RemoteSdp => null;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);
        public IPEndPoint? RemoteSignalingEndPoint => new(IPAddress.Loopback, 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged;
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }
        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived { add { } remove { } }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default)
        {
            RaiseStateChanged(SipDialogState.Terminated);
            return Task.CompletedTask;
        }

        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null)
        {
            // Mirror the real session: the BYE synchronously drives the Terminated transition, which is
            // what lets the channel-built reason win the race against Call.HangupAsync's own transition.
            RaiseStateChanged(SipDialogState.Terminated);
            return Task.CompletedTask;
        }

        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default)
        {
            RaiseStateChanged(SipDialogState.Terminated);
            return Task.CompletedTask;
        }

        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendDtmfAsync(char digit, int durationMs = 160, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendInfoAsync(string contentType, string body, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendReferAsync(string referTo, string? referredBy = null, bool suppressSubscription = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendOptionsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
