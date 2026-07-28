using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Local ICE restart (RFC 8445 §9 / #62 Punkt 2): <see cref="SipCoreCallChannel.RestartIceAsync"/>
/// re-gathers fresh credentials (new ufrag AND pwd) on the existing media socket and re-offers via a
/// direction-preserving re-INVITE. A non-ICE channel rejects the request.
/// </summary>
public sealed class SipCoreCallChannelIceRestartTests
{
    private static SipCoreCallChannel CreateChannel(ICallIceAgent? iceAgent) => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        SrtpPolicy.Disabled,
        policySource: "test",
        iceAgent: iceAgent);

    private static string PlainAnswer(int mediaPort) =>
        "v=0\r\n"
        + "o=- 2 2 IN IP4 127.0.0.1\r\n"
        + "s=peer\r\n"
        + "c=IN IP4 127.0.0.1\r\n"
        + "t=0 0\r\n"
        + $"m=audio {mediaPort} RTP/AVP 0\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + "a=sendrecv\r\n";

    private static string? ExtractAttribute(string sdp, string attribute)
    {
        foreach (var line in sdp.Split("\r\n"))
        {
            if (line.StartsWith($"a={attribute}:", StringComparison.Ordinal))
                return line[(attribute.Length + 3)..];
        }

        return null;
    }

    [Fact]
    public async Task RestartIce_reoffers_with_new_credentials_on_the_same_media_port()
    {
        var iceAgent = new SequentialIceAgent();
        using var channel = CreateChannel(iceAgent);
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);

        var offerSdp = await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);
        var initialUfrag = ExtractAttribute(offerSdp, "ice-ufrag");
        var initialPwd = ExtractAttribute(offerSdp, "ice-pwd");
        Assert.NotNull(initialUfrag);
        Assert.NotNull(initialPwd);

        var session = new RecordingReinviteSession(PlainAnswer(channel.LocalMediaPort));
        channel.AttachSession(session);

        await channel.RestartIceAsync();

        // Exactly one re-INVITE carried the restart offer.
        var reofferSdp = Assert.Single(session.ReinviteBodies);

        // RFC 8445 §9: a restart MUST change BOTH ufrag and pwd.
        var restartUfrag = ExtractAttribute(reofferSdp, "ice-ufrag");
        var restartPwd = ExtractAttribute(reofferSdp, "ice-pwd");
        Assert.NotNull(restartUfrag);
        Assert.NotNull(restartPwd);
        Assert.NotEqual(initialUfrag, restartUfrag);
        Assert.NotEqual(initialPwd, restartPwd);

        // Reference-parity (pjnath/libwebrtc variant a): the media 5-tuple is reused — the re-offer
        // advertises the same RTP port as the original offer, not a freshly bound one.
        Assert.Contains($"m=audio {channel.LocalMediaPort} ", reofferSdp, StringComparison.Ordinal);

        // The restart preserves the media direction.
        Assert.Contains("a=sendrecv", reofferSdp, StringComparison.Ordinal);

        // The agent was asked to re-gather (initial offer + restart = two descriptions).
        Assert.Equal(2, iceAgent.BuildCount);
    }

    [Fact]
    public async Task RestartIce_on_a_non_ice_call_is_rejected()
    {
        using var channel = CreateChannel(iceAgent: null);
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);

        var session = new RecordingReinviteSession(PlainAnswer(channel.LocalMediaPort));
        channel.AttachSession(session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.RestartIceAsync());
        Assert.Empty(session.ReinviteBodies);
    }

    // ICE agent that hands out a fresh ufrag/pwd on every gather so a restart's new credentials are visible.
    private sealed class SequentialIceAgent : ICallIceAgent
    {
        private int _n;

        public int BuildCount => _n;

        public Task<CallIceLocalDescription?> BuildLocalDescriptionAsync(
            IPEndPoint localEndPoint,
            System.Net.Sockets.Socket? sharedMediaSocket = null,
            IPEndPoint? videoLocalEndPoint = null,
            System.Net.Sockets.Socket? videoSharedMediaSocket = null,
            CancellationToken ct = default)
        {
            var n = ++_n;
            var host = new CallIceCandidate
            {
                Foundation = "1",
                Component = 1,
                Transport = "UDP",
                Priority = 2130706431,
                Address = localEndPoint.Address.ToString(),
                Port = localEndPoint.Port,
                Type = "host"
            };
            return Task.FromResult<CallIceLocalDescription?>(new CallIceLocalDescription
            {
                Ufrag = $"ufrag{n}",
                Pwd = $"password{n}0000000000000000",
                Candidates = [host]
            });
        }

        public Task<CallIceSelectionResult> SelectCandidatePairAsync(
            CallId callId, CallMediaParameters parameters, CancellationToken ct) =>
            Task.FromResult(new CallIceSelectionResult
            {
                State = CallIceNegotiationState.Failed,
                HasSelectedPair = false,
                ReasonCode = "not-used",
            });
    }

    // Session that records the SDP bodies passed to ReinviteAsync.
    private sealed class RecordingReinviteSession(string remoteSdp) : ISipCallSession
    {
        public List<string> ReinviteBodies { get; } = [];

        public Task ReinviteAsync(string sessionDescription, CancellationToken ct = default)
        {
            ReinviteBodies.Add(sessionDescription);
            return Task.CompletedTask;
        }

        public string CallId => "ice-restart-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:peer@127.0.0.1";
        public SipDialogState State => SipDialogState.Established;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => false;
        public string? RemoteAssertedIdentity => null;
        public string? RemoteSdp => remoteSdp;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);
        public IPEndPoint? RemoteSignalingEndPoint => new(IPAddress.Loopback, 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged { add { } remove { } }
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
