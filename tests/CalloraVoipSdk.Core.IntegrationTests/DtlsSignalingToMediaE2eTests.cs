using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// DTLS-SRTP signaling (WebRTC P1b slice 2, RFC 5763): offer/answer generation, role
/// resolution, SDES/DTLS exclusivity, and the full chain — SDP strings negotiated by the
/// real negotiator drive two real media sessions into a DTLS handshake and audio exchange.
/// </summary>
public sealed class DtlsSignalingToMediaE2eTests
{
    private const string SdesSuite = "AES_CM_128_HMAC_SHA1_80";

    // ── Offer generation ────────────────────────────────────────────────────────

    [Fact]
    public void Dtls_offer_advertises_fingerprint_actpass_and_suppresses_sdes()
    {
        var identity = Identity(DtlsCertificate.GenerateEcdsaP256());
        var offer = SdpUtilities.BuildDefaultSdp(
            new IPEndPoint(IPAddress.Loopback, 40000), hold: false,
            new SdpMediaNegotiationOptions
            {
                Dtls = identity,
                OfferDtlsSrtp = true,
                OfferSrtpCrypto = true, // must lose against DTLS — mutually exclusive
            });

        Assert.Contains("UDP/TLS/RTP/SAVPF", offer, StringComparison.Ordinal);
        Assert.Contains($"a=fingerprint:{identity.FingerprintAlgorithm} {identity.FingerprintValue}", offer, StringComparison.Ordinal);
        Assert.Contains("a=setup:actpass", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("a=crypto:", offer, StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_alone_does_not_leak_into_a_plain_offer()
    {
        var offer = SdpUtilities.BuildDefaultSdp(
            new IPEndPoint(IPAddress.Loopback, 40000), hold: false,
            new SdpMediaNegotiationOptions
            {
                Dtls = Identity(DtlsCertificate.GenerateEcdsaP256()),
                OfferDtlsSrtp = false,
                OfferSrtpCrypto = true,
            });

        Assert.DoesNotContain("a=fingerprint:", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("UDP/TLS", offer, StringComparison.Ordinal);
        Assert.Contains("a=crypto:", offer, StringComparison.Ordinal); // SDES stays in charge
    }

    // ── Answer generation ───────────────────────────────────────────────────────

    [Fact]
    public void Dtls_offer_is_answered_with_active_setup_and_own_fingerprint()
    {
        var offererIdentity = Identity(DtlsCertificate.GenerateEcdsaP256());
        var answererIdentity = Identity(DtlsCertificate.GenerateEcdsaP256());
        var offer = DtlsOfferSdp(40000, offererIdentity);

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 40002), hold: false,
            new SdpMediaNegotiationOptions { Dtls = answererIdentity });

        Assert.NotNull(answer);
        Assert.Contains("UDP/TLS/RTP/SAVPF", answer!, StringComparison.Ordinal);
        Assert.Contains("a=setup:active", answer, StringComparison.Ordinal);
        Assert.Contains($"a=fingerprint:{answererIdentity.FingerprintAlgorithm} {answererIdentity.FingerprintValue}", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("a=crypto:", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Dtls_offer_without_local_identity_is_rejected()
    {
        var offer = DtlsOfferSdp(40000, Identity(DtlsCertificate.GenerateEcdsaP256()));

        // Fail closed: a fingerprint-less answer on a UDP/TLS profile would negotiate
        // media that can never be keyed.
        Assert.Null(SdpUtilities.TryBuildNegotiatedAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 40002), hold: false, localOptions: null));
    }

    [Fact]
    public void Mixed_savp_offer_keeps_sdes_and_omits_dtls_attributes()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 40000 RTP/SAVP 0\r\n"
            + "a=rtpmap:0 PCMU/8000\r\n"
            + $"a=crypto:1 {SdesSuite} inline:{Convert.ToBase64String(new byte[30])}\r\n"
            + $"a=fingerprint:sha-256 {Identity(DtlsCertificate.GenerateEcdsaP256()).FingerprintValue}\r\n"
            + "a=setup:actpass\r\n"
            + "a=sendrecv\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 40002), hold: false,
            new SdpMediaNegotiationOptions { Dtls = Identity(DtlsCertificate.GenerateEcdsaP256()) });

        Assert.NotNull(answer);
        Assert.Contains("a=crypto:", answer!, StringComparison.Ordinal);
        Assert.DoesNotContain("a=fingerprint:", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("a=setup:", answer, StringComparison.Ordinal);
    }

    // ── Role resolution (enricher) ──────────────────────────────────────────────

    [Theory]
    [InlineData("actpass", "active", false)]  // offerer view: peer went active → we serve
    [InlineData("actpass", "passive", true)]  // offerer view: peer stayed passive → we connect
    [InlineData("active", "actpass", true)]   // answerer view: we committed active
    [InlineData("passive", "actpass", false)] // answerer view: we committed passive
    public void Enricher_resolves_dtls_role_from_setup_exchange(
        string localSetup, string remoteSetup, bool expectClient)
    {
        var localFp = Identity(DtlsCertificate.GenerateEcdsaP256());
        var remoteFp = Identity(DtlsCertificate.GenerateEcdsaP256());

        var enriched = CallMediaParametersDtlsEnricher.Enrich(
            PlainParameters(),
            remoteSdp: DtlsSdp(40000, remoteFp, remoteSetup),
            localSdp: DtlsSdp(40002, localFp, localSetup));

        Assert.True(enriched.IsDtlsNegotiated);
        Assert.Equal(expectClient, enriched.DtlsIsClient);
        Assert.Equal(remoteFp.FingerprintValue, enriched.DtlsRemoteFingerprintValue);
        Assert.Equal("sha-256", enriched.DtlsRemoteFingerprintAlgorithm);
    }

    [Fact]
    public void Enricher_leaves_parameters_untouched_without_both_fingerprints()
    {
        var untouched = CallMediaParametersDtlsEnricher.Enrich(
            PlainParameters(),
            remoteSdp: DtlsSdp(40000, Identity(DtlsCertificate.GenerateEcdsaP256()), "actpass"),
            localSdp: "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=x\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
                      + "m=audio 40002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n");

        Assert.False(untouched.IsDtlsNegotiated);
    }

    [Fact]
    public async Task Keyless_secure_negotiation_never_sends_plaintext()
    {
        // Degenerate exchange (e.g. UDP/TLS answer without fingerprint): SRTP is signaled
        // but neither SDES nor DTLS keyed the leg. The media backstop must stay silent —
        // never plain RTP.
        var peerPort = FreeUdpPort();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var datagramsAtPeer = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await peer.ReceiveAsync();
                Interlocked.Increment(ref datagramsAtPeer);
            }
        });

        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, peerPort),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            MediaProfile = "UDP/TLS/RTP/SAVPF",
            IsSrtpNegotiated = true, // signaled secure, but no keys of either kind
        };

        await using var media = new RtpCallMediaSessionFactory(NullLoggerFactory.Instance).Create(parameters);
        await media.StartAsync();

        var payload = new byte[160];
        for (var i = 0; i < 5; i++)
        {
            await media.SendFrameAsync(new CallAudioFrame(payload, 0, 160));
            await Task.Delay(20);
        }

        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref datagramsAtPeer));
    }

    // ── Full chain: negotiated SDP drives two real media sessions into DTLS ───────

    [Fact]
    public async Task Full_chain_negotiated_sdp_keys_media_and_audio_flows()
    {
        var certA = DtlsCertificate.GenerateEcdsaP256(); // offerer
        var certB = DtlsCertificate.GenerateEcdsaP256(); // answerer
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        // Offer/answer through the real negotiator, exactly as the SIP channel would.
        var offer = SdpUtilities.BuildDefaultSdp(
            new IPEndPoint(IPAddress.Loopback, portA), hold: false,
            new SdpMediaNegotiationOptions { Dtls = Identity(certA), OfferDtlsSrtp = true });
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, portB), hold: false,
            new SdpMediaNegotiationOptions { Dtls = Identity(certB) });
        Assert.NotNull(answer);

        // Each side derives media parameters solely from the exchanged SDP strings.
        var parametersA = CallMediaParametersDtlsEnricher.Enrich(
            SdpUtilities.TryParseMediaParameters(answer!, new IPEndPoint(IPAddress.Loopback, portA))!,
            remoteSdp: answer!, localSdp: offer);
        var parametersB = CallMediaParametersDtlsEnricher.Enrich(
            SdpUtilities.TryParseMediaParameters(offer, new IPEndPoint(IPAddress.Loopback, portB))!,
            remoteSdp: offer, localSdp: answer);

        Assert.True(parametersA.IsDtlsNegotiated);
        Assert.True(parametersB.IsDtlsNegotiated);
        Assert.False(parametersA.DtlsIsClient); // answer committed active → offerer serves
        Assert.True(parametersB.DtlsIsClient);

        await using var mediaA = new RtpCallMediaSessionFactory(
                NullLoggerFactory.Instance, bridgeTapCodec: null,
                new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certA)
            .Create(parametersA);
        await using var mediaB = new RtpCallMediaSessionFactory(
                NullLoggerFactory.Instance, bridgeTapCodec: null,
                new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certB)
            .Create(parametersB);

        var frameReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediaB.FrameReceived += frame => frameReceived.TrySetResult(frame.Payload.ToArray());

        await mediaB.StartAsync();
        await mediaA.StartAsync();

        var payload = new byte[160];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(255 - i);

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!frameReceived.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await mediaA.SendFrameAsync(new CallAudioFrame(payload, 0, 160), overall.Token);
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(payload, await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static SdpDtlsNegotiationOptions Identity(DtlsCertificate certificate) => new()
    {
        FingerprintAlgorithm = certificate.Fingerprint.Algorithm,
        FingerprintValue = certificate.Fingerprint.Value,
    };

    private static string DtlsOfferSdp(int port, SdpDtlsNegotiationOptions identity) =>
        DtlsSdp(port, identity, "actpass");

    private static string DtlsSdp(int port, SdpDtlsNegotiationOptions identity, string setup) =>
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
        + $"m=audio {port} UDP/TLS/RTP/SAVPF 0\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + $"a=fingerprint:{identity.FingerprintAlgorithm} {identity.FingerprintValue}\r\n"
        + $"a=setup:{setup}\r\n"
        + "a=sendrecv\r\n";

    private static CallMediaParameters PlainParameters() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 40002),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 40000),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
