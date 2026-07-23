using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Early media (F011 slice 3b): when a provisional 180/183 carried an SDP, the channel publishes media
/// parameters on the Ringing transition so a receive-only media session starts before the 200 OK. The
/// later Established transition sees an unchanged signature (Asterisk Progress-&gt;Answer) and does not
/// re-publish. A parse failure on the early SDP releases the publish guard so the 200-OK path runs
/// normally. Channel-level coverage of the guard logic (the interop test is Docker-gated).
/// </summary>
public sealed class SipCoreCallChannelEarlyMediaTests
{
    private static string PlainAnswer(int mediaPort, int payloadType = 0, string codec = "PCMU") =>
        "v=0\r\n"
        + "o=- 2 2 IN IP4 127.0.0.1\r\n"
        + "s=peer\r\n"
        + "c=IN IP4 127.0.0.1\r\n"
        + "t=0 0\r\n"
        + $"m=audio {mediaPort} RTP/AVP {payloadType}\r\n"
        + $"a=rtpmap:{payloadType} {codec}/8000\r\n"
        + "a=sendrecv\r\n";

    private static SipCoreCallChannel CreateChannel(SrtpPolicy policy = SrtpPolicy.Optional) => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        policy,
        policySource: "test");

    // Builds an offerer channel (our own key/offer retained) with the MediaParametersNegotiated tap wired,
    // then attaches the session WITHOUT establishing it (State stays Ringing-observable via RaiseStateChanged).
    private static async Task<(SipCoreCallChannel Channel, EarlyMediaSession Session, List<CallMediaParameters> Fires)>
        ArrangeOffererAsync(string? earlyMediaSdp, string remoteSdp)
    {
        var channel = CreateChannel();
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);

        var fires = new List<CallMediaParameters>();
        channel.MediaParametersNegotiated += (_, p) => fires.Add(p);

        var session = new EarlyMediaSession(remoteSdp, earlyMediaSdp);
        channel.AttachSession(session);
        return (channel, session, fires);
    }

    [Fact]
    public async Task Early_media_sdp_on_ringing_publishes_once_before_established()
    {
        var (channel, session, fires) = await ArrangeOffererAsync(
            earlyMediaSdp: PlainAnswer(30000),
            remoteSdp: PlainAnswer(30000));
        using var _ = channel;

        // Attach on a non-established session must not publish yet (no Ringing seen).
        Assert.Empty(fires);

        session.RaiseStateChanged(SipDialogState.Ringing);

        Assert.Single(fires); // early-media publish fired exactly once, before any Established transition
        Assert.Equal(30000, fires[0].RemoteEndPoint.Port);
    }

    [Fact]
    public async Task Identical_answer_on_established_does_not_republish_after_early_publish()
    {
        var sdp = PlainAnswer(30000);
        var (channel, session, fires) = await ArrangeOffererAsync(earlyMediaSdp: sdp, remoteSdp: sdp);
        using var _ = channel;

        session.RaiseStateChanged(SipDialogState.Ringing);
        Assert.Single(fires); // early publish

        // 200 OK with the identical SDP (Asterisk Progress->Answer): rekey path sees an unchanged
        // signature and skips — the early-media session runs through without a rebuild.
        session.RaiseStateChanged(SipDialogState.Established);

        Assert.Single(fires); // no second fire
    }

    [Fact]
    public async Task Unparsable_early_sdp_releases_guard_so_established_publishes()
    {
        var (channel, session, fires) = await ArrangeOffererAsync(
            earlyMediaSdp: "not-a-valid-sdp-body",
            remoteSdp: PlainAnswer(30000));
        using var _ = channel;

        // Ringing: the early SDP is non-null but unparsable → publish fails and the guard is released,
        // so nothing fires yet.
        session.RaiseStateChanged(SipDialogState.Ringing);
        Assert.Empty(fires);

        // 200 OK with a valid answer: the guard was released, so the Established publish now fires.
        session.RaiseStateChanged(SipDialogState.Established);

        Assert.Single(fires);
        Assert.Equal(30000, fires[0].RemoteEndPoint.Port);
    }

    // Session whose state transition can be raised and that exposes a settable EarlyMediaSdp
    // (default-interface member on ISipCallSession) alongside the remote answer SDP.
    private sealed class EarlyMediaSession(string remoteSdp, string? earlyMediaSdp) : ISipCallSession
    {
        private string _remoteSdp = remoteSdp;

        public void SetRemoteSdp(string sdp) => _remoteSdp = sdp;

        public void RaiseStateChanged(SipDialogState newState) =>
            StateChanged?.Invoke(this, new SipDialogStateChangedEventArgs(State, newState, null));

        public string? EarlyMediaSdp => earlyMediaSdp;

        public string CallId => "e2e-early-media-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:peer@127.0.0.1";
        public SipDialogState State => SipDialogState.Ringing;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => false;
        public string? RemoteAssertedIdentity => null;
        public string? RemoteSdp => _remoteSdp;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);
        public IPEndPoint? RemoteSignalingEndPoint => new(IPAddress.Loopback, 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged;
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }
        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived { add { } remove { } }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default) => throw new NotSupportedException();
        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SendDtmfAsync(char digit, int durationMs = 160, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SendReferAsync(string referTo, string? referredBy = null, bool suppressSubscription = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SendOptionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
