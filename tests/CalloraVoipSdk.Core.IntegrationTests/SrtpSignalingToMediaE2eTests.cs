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
/// SRTP chain E2E (package S3): from a raw SAVP offer STRING through the production
/// components — <see cref="SipCoreCallChannel.AnswerAsync"/> with the real SDP negotiator,
/// the fired <see cref="CallMediaParameters"/>, and a real
/// <see cref="RtpCallMediaSessionFactory"/> media session — to encrypted packets on the
/// wire, against a test peer whose contexts are derived solely from the exchanged SDP
/// strings. This covers the glue S2 left untested: answer-SDP retention, channel
/// enrichment, and context creation from real negotiated parameters.
/// </summary>
public sealed class SrtpSignalingToMediaE2eTests
{
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";

    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static string OfferSdp(int mediaPort, string profile, string? cryptoLine) =>
        "v=0\r\n"
        + "o=- 1 1 IN IP4 127.0.0.1\r\n"
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

    private static async Task<(CallMediaParameters Parameters, string AnswerSdp)> AnswerAsync(
        SipCoreCallChannel channel, string offerSdp)
    {
        var session = new FakeInboundSession(offerSdp);
        CallMediaParameters? fired = null;
        channel.MediaParametersNegotiated += (_, p) => fired = p;

        channel.AttachSession(session);
        await channel.AnswerAsync(CancellationToken.None);

        Assert.NotNull(fired);
        Assert.NotNull(session.CapturedAnswerSdp);
        return (fired!, session.CapturedAnswerSdp!);
    }

    // ── K1: SAVP offer → parameters carry both keys, consistent with both SDPs ──

    [Fact]
    public async Task Savp_offer_produces_media_parameters_with_both_keys()
    {
        var offerKey = InlineKey(1);
        using var channel = CreateChannel();
        var (parameters, answerSdp) = await AnswerAsync(
            channel, OfferSdp(30000, "RTP/SAVP", $"1 {Suite} {offerKey}"));

        Assert.Equal(Suite, parameters.SrtpSuite);
        Assert.Equal(offerKey, parameters.SrtpRemoteKeyParams);

        var answerCrypto = SdpUtilities.TryExtractAudioCrypto(answerSdp);
        Assert.NotNull(answerCrypto);
        Assert.Equal(answerCrypto!.KeyParams, parameters.SrtpLocalKeyParams);
        Assert.NotEqual(offerKey, parameters.SrtpLocalKeyParams);
        Assert.True(parameters.IsSrtpNegotiated);
    }

    // ── K2: only one direction usable → no SRTP fields (never half-encrypted) ──

    [Fact]
    public async Task Unsupported_crypto_on_avp_offer_yields_plain_parameters()
    {
        // The offer carries a crypto line we cannot answer (unsupported suite on an AVP
        // profile): S1 falls back to a plain answer, so only the remote direction would
        // have a key — the channel must publish no SRTP fields at all. A literal suite
        // MISMATCH between offer and answer is unreachable through the real negotiator
        // (the answer always mirrors the chosen suite); this one-sided case is the
        // reachable variant of that guard.
        using var channel = CreateChannel();
        var (parameters, answerSdp) = await AnswerAsync(
            channel, OfferSdp(30002, "RTP/AVP", $"1 F8_128_HMAC_SHA1_80 {InlineKey(5)}"));

        Assert.Null(SdpUtilities.TryExtractAudioCrypto(answerSdp));
        Assert.Null(parameters.SrtpSuite);
        Assert.Null(parameters.SrtpLocalKeyParams);
        Assert.Null(parameters.SrtpRemoteKeyParams);
    }

    // ── K4: plain AVP offer → plain parameters (live sipgate pattern) ──────────

    [Fact]
    public async Task Plain_avp_offer_yields_plain_parameters()
    {
        using var channel = CreateChannel();
        var (parameters, answerSdp) = await AnswerAsync(
            channel, OfferSdp(30004, "RTP/AVP", cryptoLine: null));

        Assert.Null(parameters.SrtpSuite);
        Assert.Null(parameters.SrtpLocalKeyParams);
        Assert.Null(parameters.SrtpRemoteKeyParams);
        Assert.Null(SdpUtilities.TryExtractAudioCrypto(answerSdp));
        Assert.False(parameters.IsSrtpNegotiated);
    }

    // ── K3: full chain — encrypted both ways through the real media session ────

    [Fact]
    public async Task Full_chain_encrypts_and_decrypts_via_real_media_session()
    {
        var offerKey = InlineKey(40);
        using var peerSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)peerSocket.Client.LocalEndPoint!).Port;

        using var channel = CreateChannel();
        var (parameters, answerSdp) = await AnswerAsync(
            channel, OfferSdp(peerPort, "RTP/SAVP", $"1 {Suite} {offerKey}"));

        // Peer derives its contexts SOLELY from the exchanged SDP strings — exactly what
        // a real SIP peer would do: decrypt with our answer key, encrypt with its own.
        var answerKey = SdpUtilities.TryExtractAudioCrypto(answerSdp)!.KeyParams;
        var peerInbound = new SrtpContext(SrtpKeyMaterial.ParseInline(answerKey, SrtpCryptoSuite.AesCm128HmacSha1_80));
        var peerOutbound = new SrtpContext(SrtpKeyMaterial.ParseInline(offerKey, SrtpCryptoSuite.AesCm128HmacSha1_80));

        await using var media = new RtpCallMediaSessionFactory(NullLoggerFactory.Instance).Create(parameters);
        var frameReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        media.FrameReceived += f => frameReceived.TrySetResult(f.Payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await media.StartAsync(cts.Token);

        // SDK → peer: the frame must arrive as ciphertext that only the answer key opens.
        var outboundPayload = new byte[160];
        Array.Fill(outboundPayload, (byte)0x5A);
        await media.SendFrameAsync(new CallAudioFrame(outboundPayload, 0, 160), cts.Token);

        var wire = (await peerSocket.ReceiveAsync(cts.Token)).Buffer;
        Assert.Equal(12 + outboundPayload.Length + 10, wire.Length);
        Assert.NotEqual(outboundPayload, wire[12..(12 + outboundPayload.Length)]);
        var decrypted = new RtpPacketCodec().Decode(peerInbound.Unprotect(wire));
        Assert.Equal(outboundPayload, decrypted.Payload.ToArray());

        // Peer → SDK: encrypted packets must surface as plaintext frames. Send a paced
        // burst so the jitter buffer's playout loop delivers.
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

    // ── Minimal inbound session fake: only what the answer path actually touches ──

    private sealed class FakeInboundSession(string remoteSdp) : ISipCallSession
    {
        public string? CapturedAnswerSdp { get; private set; }

        public string CallId => "e2e-srtp-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:peer@127.0.0.1";
        public SipDialogState State { get; private set; } = SipDialogState.Ringing;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => true;
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
        {
            CapturedAnswerSdp = sessionDescription;
            State = SipDialogState.Established;
            return Task.CompletedTask;
        }

        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default)
            => throw new InvalidOperationException($"E2E test peer rejected with {statusCode}.");

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
