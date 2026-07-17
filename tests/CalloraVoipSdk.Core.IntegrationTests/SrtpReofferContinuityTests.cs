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
/// Hold/unhold re-offer SRTP continuity (SDES follow-up): a re-INVITE on a running SRTP
/// call must keep RTP/SAVP and re-advertise the SAME live outbound key — never downgrade to
/// plain RTP and never rekey (which would break the running media context). A plain call
/// re-offers plain RTP unchanged.
/// </summary>
public sealed class SrtpReofferContinuityTests
{
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";

    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static string PeerAnswerSdp(int mediaPort, string profile, string? cryptoLine) =>
        "v=0\r\n"
        + "o=- 2 2 IN IP4 127.0.0.1\r\n"
        + "s=peer\r\n"
        + "c=IN IP4 127.0.0.1\r\n"
        + "t=0 0\r\n"
        + $"m=audio {mediaPort} {profile} 0\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + (cryptoLine is null ? string.Empty : $"a=crypto:{cryptoLine}\r\n")
        + "a=sendrecv\r\n";

    private static SipCoreCallChannel CreateChannel() => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        SrtpPolicy.Optional,
        policySource: "test");

    // Establishes a call through the real channel and returns the retained offer key (null
    // when the peer declined SRTP). The session then captures hold/unhold re-offer SDP.
    private static async Task<(FakeReofferSession Session, string? OfferKey)> EstablishAsync(
        SipCoreCallChannel channel, string peerAnswerProfile, string? peerCryptoLine)
    {
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        var offerSdp = await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);
        var offerKey = SdpUtilities.TryExtractAudioCrypto(offerSdp)?.KeyParams;

        var session = new FakeReofferSession(
            PeerAnswerSdp(channel.LocalMediaPort, peerAnswerProfile, peerCryptoLine));
        channel.AttachSession(session);
        return (session, offerKey);
    }

    [Fact]
    public async Task Hold_reoffer_keeps_savp_and_reuses_live_key()
    {
        using var channel = CreateChannel();
        var (session, offerKey) = await EstablishAsync(channel, "RTP/SAVP", $"1 {Suite} {InlineKey(70)}");
        Assert.NotNull(offerKey);

        await channel.HoldAsync();

        Assert.NotNull(session.CapturedHoldSdp);
        Assert.Contains("RTP/SAVP", session.CapturedHoldSdp!, StringComparison.Ordinal);
        Assert.Contains("a=sendonly", session.CapturedHoldSdp!, StringComparison.Ordinal);
        // Same key as the running context → peer keeps decrypting, no rekey.
        Assert.Equal(offerKey, SdpUtilities.TryExtractAudioCrypto(session.CapturedHoldSdp)!.KeyParams);
    }

    [Fact]
    public async Task Unhold_reoffer_keeps_savp_and_reuses_live_key()
    {
        using var channel = CreateChannel();
        var (session, offerKey) = await EstablishAsync(channel, "RTP/SAVP", $"1 {Suite} {InlineKey(70)}");

        await channel.UnholdAsync();

        Assert.NotNull(session.CapturedUnholdSdp);
        Assert.Contains("RTP/SAVP", session.CapturedUnholdSdp!, StringComparison.Ordinal);
        Assert.Contains("a=sendrecv", session.CapturedUnholdSdp!, StringComparison.Ordinal);
        Assert.Equal(offerKey, SdpUtilities.TryExtractAudioCrypto(session.CapturedUnholdSdp)!.KeyParams);
    }

    [Fact]
    public async Task Hold_reoffer_stays_plain_when_call_is_not_srtp()
    {
        using var channel = CreateChannel();
        // Peer declined SRTP → the call runs plain; no live key to reuse.
        var (session, _) = await EstablishAsync(channel, "RTP/AVP", peerCryptoLine: null);

        await channel.HoldAsync();

        Assert.NotNull(session.CapturedHoldSdp);
        Assert.DoesNotContain("SAVP", session.CapturedHoldSdp!, StringComparison.Ordinal);
        Assert.DoesNotContain("a=crypto", session.CapturedHoldSdp!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hold_reoffer_reuses_the_live_video_key()
    {
        // A video-enabled SDES call re-advertises the SAME per-m-line video key on hold — the
        // running SRTP video stream must not be rekeyed (RFC 4568 per-m-line keying).
        using var channel = CreateVideoChannel();
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        var offerSdp = await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);
        var videoOfferKey = SdpUtilities.TryExtractVideoCrypto(offerSdp)?.KeyParams;
        Assert.NotNull(videoOfferKey);

        var session = new FakeReofferSession(PeerAnswerSdpWithVideo(channel.LocalMediaPort, videoPort: 6004));
        channel.AttachSession(session);

        await channel.HoldAsync();

        Assert.NotNull(session.CapturedHoldSdp);
        var holdVideoCrypto = SdpUtilities.TryExtractVideoCrypto(session.CapturedHoldSdp);
        Assert.NotNull(holdVideoCrypto);
        Assert.Equal(videoOfferKey, holdVideoCrypto!.KeyParams); // reused, not rekeyed
    }

    private static SipCoreCallChannel CreateVideoChannel() => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        SrtpPolicy.Optional,
        policySource: "test",
        iceAgent: null,
        preferredCodecNames: null,
        advertisedPublicMediaAddress: null,
        dtlsOptions: null,
        offerDtlsSrtp: false,
        enableVideo: true);

    private static string PeerAnswerSdpWithVideo(int mediaPort, int videoPort) =>
        "v=0\r\no=- 2 2 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
        + $"m=audio {mediaPort} RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
        + $"a=crypto:1 {Suite} {InlineKey(70)}\r\na=sendrecv\r\n"
        + $"m=video {videoPort} RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n"
        + $"a=crypto:1 {Suite} {InlineKey(90)}\r\n";

    // Established outbound session that captures hold/unhold re-offer SDP.
    private sealed class FakeReofferSession(string remoteSdp) : ISipCallSession
    {
        public string? CapturedHoldSdp { get; private set; }
        public string? CapturedUnholdSdp { get; private set; }

        public string CallId => "e2e-srtp-reoffer-call";
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

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default)
        {
            CapturedHoldSdp = sessionDescription;
            return Task.CompletedTask;
        }

        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default)
        {
            CapturedUnholdSdp = sessionDescription;
            return Task.CompletedTask;
        }

        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null)
            => Task.CompletedTask;

        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default)
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
