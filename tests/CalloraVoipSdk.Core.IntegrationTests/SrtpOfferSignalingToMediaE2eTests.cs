using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SRTP offer-side chain E2E (SDES follow-up "Offer-SDES"): the SDK is the offerer.
/// <see cref="SipCoreCallChannel.BuildOfferSdpAsync"/> emits an RTP/SAVP offer with our own
/// <c>a=crypto</c> line (outbound encrypt key); the peer answers with its own key (inbound
/// decrypt). Covers what the answer-side E2E did not: offer-SDP emission + retention, and
/// channel enrichment recovering both keys from the exchanged strings on the outbound leg.
/// </summary>
public sealed class SrtpOfferSignalingToMediaE2eTests
{
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";

    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    // The peer's answer, keyed with its own crypto line (or plain when cryptoLine is null).
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

    private static SipCoreCallChannel CreateChannel(SrtpPolicy policy = SrtpPolicy.Optional) => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        policy,
        policySource: "test");

    // Builds an offer through the real channel, then attaches an already-established
    // outbound session carrying the peer's answer — exactly the outbound sequence.
    private static async Task<(string OfferSdp, CallMediaParameters Parameters)> OfferAndEstablishAsync(
        SipCoreCallChannel channel, Func<int, string> buildPeerAnswer)
    {
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort);
        var offerSdp = await channel.BuildOfferSdpAsync(localEndPoint, hold: false, CancellationToken.None);

        CallMediaParameters? fired = null;
        channel.MediaParametersNegotiated += (_, p) => fired = p;

        var session = new FakeEstablishedSession(buildPeerAnswer(channel.LocalMediaPort));
        channel.AttachSession(session);

        Assert.NotNull(fired);
        return (offerSdp, fired!);
    }

    // ── Offer emission: policy that wants SRTP produces RTP/SAVP + a=crypto ────────

    [Theory]
    [InlineData(SrtpPolicy.Optional)]
    [InlineData(SrtpPolicy.Required)]
    public async Task Offer_advertises_savp_and_crypto_when_policy_wants_srtp(SrtpPolicy policy)
    {
        using var channel = CreateChannel(policy);
        var offerSdp = await channel.BuildOfferSdpAsync(
            new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort), hold: false, CancellationToken.None);

        Assert.Contains("RTP/SAVP", offerSdp, StringComparison.Ordinal);
        var crypto = SdpUtilities.TryExtractAudioCrypto(offerSdp);
        Assert.NotNull(crypto);
        Assert.Equal(Suite, crypto!.CryptoSuite);
        Assert.StartsWith("inline:", crypto.KeyParams, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offer_is_plain_rtp_when_policy_disabled()
    {
        using var channel = CreateChannel(SrtpPolicy.Disabled);
        var offerSdp = await channel.BuildOfferSdpAsync(
            new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort), hold: false, CancellationToken.None);

        Assert.DoesNotContain("SAVP", offerSdp, StringComparison.Ordinal);
        Assert.DoesNotContain("a=crypto", offerSdp, StringComparison.Ordinal);
        Assert.Null(SdpUtilities.TryExtractAudioCrypto(offerSdp));
    }

    // ── O1: SAVP answer to our offer → parameters carry both keys, both consistent ─

    [Fact]
    public async Task Savp_answer_produces_media_parameters_with_both_keys()
    {
        var peerKey = InlineKey(70);
        using var channel = CreateChannel();
        var (offerSdp, parameters) = await OfferAndEstablishAsync(
            channel, port => PeerAnswerSdp(port, "RTP/SAVP", $"1 {Suite} {peerKey}"));

        var offerKey = SdpUtilities.TryExtractAudioCrypto(offerSdp)!.KeyParams;

        Assert.True(parameters.IsSrtpNegotiated);
        Assert.Equal(Suite, parameters.SrtpSuite);
        // SRTCP is protected together with SRTP once both SDES keys are present (RFC 3711 §3.4).
        Assert.True(parameters.IsSrtcpEncrypted);
        // Our offer key is the outbound encrypt key; the peer's answer key decrypts inbound.
        Assert.Equal(offerKey, parameters.SrtpLocalKeyParams);
        Assert.Equal(peerKey, parameters.SrtpRemoteKeyParams);
        Assert.NotEqual(offerKey, peerKey);
    }

    // ── O2: peer answered plain RTP (declined SRTP) → no SRTP fields, never half ───

    [Fact]
    public async Task Plain_answer_to_srtp_offer_yields_plain_parameters()
    {
        using var channel = CreateChannel(SrtpPolicy.Optional);
        var (_, parameters) = await OfferAndEstablishAsync(
            channel, port => PeerAnswerSdp(port, "RTP/AVP", cryptoLine: null));

        Assert.False(parameters.IsSrtpNegotiated);
        Assert.Null(parameters.SrtpSuite);
        Assert.False(parameters.IsSrtcpEncrypted);
        Assert.Null(parameters.SrtpLocalKeyParams);
        Assert.Null(parameters.SrtpRemoteKeyParams);
    }

    // ── O3: full chain — encrypted both ways through the real media session ────────

    [Fact]
    public async Task Full_chain_encrypts_and_decrypts_via_real_media_session()
    {
        using var peerSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)peerSocket.Client.LocalEndPoint!).Port;
        var peerKey = InlineKey(90);

        using var channel = CreateChannel();
        var (offerSdp, parameters) = await OfferAndEstablishAsync(
            channel, _ => PeerAnswerSdp(peerPort, "RTP/SAVP", $"1 {Suite} {peerKey}"));

        // Peer derives its contexts SOLELY from the exchanged SDP strings: decrypt our
        // stream with our offer key, encrypt its stream with its own answer key.
        var offerKey = SdpUtilities.TryExtractAudioCrypto(offerSdp)!.KeyParams;
        var peerInbound = new SrtpContext(SrtpKeyMaterial.ParseInline(offerKey, SrtpCryptoSuite.AesCm128HmacSha1_80));
        var peerOutbound = new SrtpContext(SrtpKeyMaterial.ParseInline(peerKey, SrtpCryptoSuite.AesCm128HmacSha1_80));

        await using var media = new RtpCallMediaSessionFactory(NullLoggerFactory.Instance).Create(parameters);
        var frameReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        media.FrameReceived += f => frameReceived.TrySetResult(f.Payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await media.StartAsync(cts.Token);

        // SDK → peer: the frame must arrive as ciphertext that only our offer key opens.
        var outboundPayload = new byte[160];
        Array.Fill(outboundPayload, (byte)0x5A);
        await media.SendFrameAsync(new CallAudioFrame(outboundPayload, 0, 160), cts.Token);

        var wire = (await peerSocket.ReceiveAsync(cts.Token)).Buffer;
        Assert.Equal(12 + outboundPayload.Length + 10, wire.Length);
        Assert.NotEqual(outboundPayload, wire[12..(12 + outboundPayload.Length)]);
        var decrypted = new RtpPacketCodec().Decode(peerInbound.Unprotect(wire));
        Assert.Equal(outboundPayload, decrypted.Payload.ToArray());

        // Peer → SDK: encrypted packets must surface as plaintext frames. Paced burst so
        // the jitter buffer's playout loop delivers.
        var peerPayload = new byte[160];
        Array.Fill(peerPayload, (byte)0xC3);
        var codec = new RtpPacketCodec();
        for (ushort seq = 1; seq <= 8 && !frameReceived.Task.IsCompleted; seq++)
        {
            var packet = peerOutbound.Protect(codec.Encode(new RtpPacket
            {
                PayloadType = 0,
                SequenceNumber = seq,
                Timestamp = (uint)(seq * 160),
                Ssrc = 0xCAFE,
                Payload = peerPayload
            }));
            await peerSocket.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, parameters.LocalEndPoint.Port));
            await Task.Delay(20, cts.Token);
        }

        var inboundFrame = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Equal(peerPayload, inboundFrame);
    }

    // ── Minimal established (outbound) session fake: only what publication touches ──

    private sealed class FakeEstablishedSession(string remoteSdp) : ISipCallSession
    {
        public string CallId => "e2e-srtp-offer-call";
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

        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null)
            => Task.CompletedTask;

        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default)
            => throw new NotSupportedException();

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
