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
/// Re-INVITE rekey: a second Established transition that changes the negotiated media (a new
/// peer SDES key) re-publishes MediaParametersNegotiated so the orchestrator rebuilds the media
/// session with the new keys. An identical re-INVITE/retransmission does not re-publish, and the
/// initial publish path is unchanged (fires exactly once).
/// </summary>
public sealed class SipCoreCallChannelRekeyTests
{
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";

    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static string PeerAnswer(int mediaPort, string profile, string? cryptoLine) =>
        "v=0\r\n"
        + "o=- 2 2 IN IP4 127.0.0.1\r\n"
        + "s=peer\r\n"
        + "c=IN IP4 127.0.0.1\r\n"
        + "t=0 0\r\n"
        + $"m=audio {mediaPort} {profile} 0\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + (cryptoLine is null ? string.Empty : $"a=crypto:{cryptoLine}\r\n")
        + "a=sendrecv\r\n";

    private static SipCoreCallChannel CreateChannel(SrtpPolicy policy = SrtpPolicy.Optional) => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        policy,
        policySource: "test");

    private static async Task<(SipCoreCallChannel Channel, RekeyableSession Session, List<CallMediaParameters> Fires)>
        EstablishSrtpAsync(string peerCryptoLine)
    {
        var channel = CreateChannel();
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);

        var fires = new List<CallMediaParameters>();
        channel.MediaParametersNegotiated += (_, p) => fires.Add(p);

        var session = new RekeyableSession(PeerAnswer(channel.LocalMediaPort, "RTP/SAVP", peerCryptoLine));
        channel.AttachSession(session); // initial publish
        return (channel, session, fires);
    }

    [Fact]
    public async Task Reinvite_with_new_peer_key_republishes_media_parameters()
    {
        var peerKey1 = InlineKey(70);
        var peerKey2 = InlineKey(120);
        var (channel, session, fires) = await EstablishSrtpAsync($"1 {Suite} {peerKey1}");
        using var _ = channel;

        Assert.Single(fires);
        Assert.Equal(peerKey1, fires[0].SrtpRemoteKeyParams);

        // Peer re-INVITE (e.g. unhold) whose answer carries a fresh key → rekey.
        session.SetRemoteSdp(PeerAnswer(channel.LocalMediaPort, "RTP/SAVP", $"1 {Suite} {peerKey2}"));
        session.RaiseStateChanged(SipDialogState.Established);

        Assert.Equal(2, fires.Count);
        Assert.Equal(peerKey2, fires[1].SrtpRemoteKeyParams);
        Assert.NotEqual(fires[0].SrtpRemoteKeyParams, fires[1].SrtpRemoteKeyParams);
        // Our own outbound key stays stable across the rekey (hold/unhold continuity).
        Assert.Equal(fires[0].SrtpLocalKeyParams, fires[1].SrtpLocalKeyParams);
    }

    [Fact]
    public async Task Reinvite_with_identical_media_does_not_republish()
    {
        var (channel, session, fires) = await EstablishSrtpAsync($"1 {Suite} {InlineKey(70)}");
        using var _ = channel;

        Assert.Single(fires);

        // Same SDP again (a re-INVITE that changes nothing, or a 200-OK retransmission).
        session.RaiseStateChanged(SipDialogState.Established);
        session.RaiseStateChanged(SipDialogState.Established);

        Assert.Single(fires); // no churn
    }

    [Fact]
    public async Task Reinvite_changing_codec_republishes()
    {
        var key = InlineKey(70);
        var (channel, session, fires) = await EstablishSrtpAsync($"1 {Suite} {key}");
        using var _ = channel;

        Assert.Single(fires);
        Assert.Equal("PCMU", fires[0].CodecName);

        // Re-INVITE keeps the key but switches the codec to PCMA → signature changes.
        session.SetRemoteSdp(
            "v=0\r\no=- 3 3 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + $"m=audio {channel.LocalMediaPort} RTP/SAVP 8\r\na=rtpmap:8 PCMA/8000\r\n"
            + $"a=crypto:1 {Suite} {key}\r\na=sendrecv\r\n");
        session.RaiseStateChanged(SipDialogState.Established);

        Assert.Equal(2, fires.Count);
        Assert.Equal("PCMA", fires[1].CodecName);
    }

    [Fact]
    public async Task Reinvite_upgrading_plain_to_srtp_republishes()
    {
        using var channel = CreateChannel();
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);

        var fires = new List<CallMediaParameters>();
        channel.MediaParametersNegotiated += (_, p) => fires.Add(p);

        // Initial: peer declines SRTP (plain RTP/AVP).
        var session = new RekeyableSession(PeerAnswer(channel.LocalMediaPort, "RTP/AVP", cryptoLine: null));
        channel.AttachSession(session);

        Assert.Single(fires);
        Assert.False(fires[0].IsSrtpNegotiated);

        // Re-INVITE now negotiates SRTP → signature changes (suite null → set).
        session.SetRemoteSdp(PeerAnswer(channel.LocalMediaPort, "RTP/SAVP", $"1 {Suite} {InlineKey(70)}"));
        session.RaiseStateChanged(SipDialogState.Established);

        Assert.Equal(2, fires.Count);
        Assert.True(fires[1].IsSrtpNegotiated);
    }

    [Fact]
    public async Task Initial_publish_still_fires_exactly_once()
    {
        var (channel, _, fires) = await EstablishSrtpAsync($"1 {Suite} {InlineKey(70)}");
        using var _c = channel;

        Assert.Single(fires);
        Assert.True(fires[0].IsSrtpNegotiated);
    }

    [Fact]
    public async Task Inbound_reinvite_rekeys_both_directions_via_session_local_sdp()
    {
        var peerKey1 = InlineKey(70);
        var (channel, session, fires) = await EstablishSrtpAsync($"1 {Suite} {peerKey1}");
        using var _ = channel;

        Assert.Single(fires);
        var initialLocalKey = fires[0].SrtpLocalKeyParams;

        // Simulate an inbound re-INVITE: the inbound service recorded a fresh peer offer and the
        // fresh answer we sent (new local key) on the session before raising Established.
        var newLocalKey = InlineKey(150);
        var peerKey2 = InlineKey(200);
        session.SetLocalSdp(PeerAnswer(channel.LocalMediaPort, "RTP/SAVP", $"1 {Suite} {newLocalKey}"));
        session.SetRemoteSdp(PeerAnswer(channel.LocalMediaPort, "RTP/SAVP", $"1 {Suite} {peerKey2}"));
        session.RaiseStateChanged(SipDialogState.Established);

        Assert.Equal(2, fires.Count);
        // Full rekey: our own key comes from the session's local SDP, peer's from its new offer.
        Assert.Equal(newLocalKey, fires[1].SrtpLocalKeyParams);
        Assert.Equal(peerKey2, fires[1].SrtpRemoteKeyParams);
        Assert.NotEqual(initialLocalKey, fires[1].SrtpLocalKeyParams);
    }

    // Session whose state transition can be raised and whose remote/local SDP can be swapped.
    private sealed class RekeyableSession(string remoteSdp) : ISipCallSession
    {
        private string _remoteSdp = remoteSdp;
        private string? _localSdp;

        public void SetRemoteSdp(string sdp) => _remoteSdp = sdp;
        public void SetLocalSdp(string sdp) => _localSdp = sdp;
        public string? LocalSdp => _localSdp;

        public void RaiseStateChanged(SipDialogState newState) =>
            StateChanged?.Invoke(this, new SipDialogStateChangedEventArgs(State, newState, null));

        public string CallId => "e2e-rekey-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:peer@127.0.0.1";
        public SipDialogState State => SipDialogState.Established;
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
