using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// HARD-S1: a DTLS-keyed profile (<c>UDP/TLS/*</c>) is fingerprint-keyed, so a stray <c>a=crypto</c> on
/// it must be ignored (RFC 5763) — for the audio m-line exactly as for video. Before the fix the audio
/// path selected the spurious SDES suite and then tripped the DTLS-profile fail-closed check, wrongly
/// rejecting the audio m-line while video keyed correctly via DTLS. These pin audio parity with video.
/// </summary>
public sealed class SdpDtlsProfileSdesAnswerTests
{
    private const string SpuriousKey = "inline:WVNfX19TZWNyZXRLZXlfMTZCX1NhbHRfMTRCeXQ=";
    private const string RemoteFingerprint = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Parse("192.0.2.10"), 40000);
    private static readonly SdpCodecDefinition Pcmu = new() { PayloadType = 0, Name = "PCMU", ClockRate = 8000 };

    private static SdpSessionDescription DtlsAudioOfferWithSpuriousCrypto() => new()
    {
        OriginAddress = "203.0.113.5",
        ConnectionAddress = "203.0.113.5",
        Media =
        [
            new SdpMediaDescription
            {
                MediaType = "audio",
                Port = 20000,
                Profile = "UDP/TLS/RTP/SAVP",
                Codecs = [Pcmu],
                Direction = SdpMediaDirection.SendRecv,
                Fingerprint = new SdpFingerprint { Algorithm = "sha-256", Value = RemoteFingerprint },
                DtlsSetup = "actpass",
                Crypto =
                [
                    new SdpCryptoAttribute { Tag = 1, CryptoSuite = "AES_CM_128_HMAC_SHA1_80", KeyParams = SpuriousKey }
                ]
            }
        ]
    };

    private static SdpMediaOptions LocalDtlsOptions() => new()
    {
        Dtls = new SdpDtlsParameters
        {
            Algorithm = "sha-256",
            Fingerprint = "99:88:77:66:55:44:33:22:11:00:FF:EE:DD:CC:BB:AA"
        }
    };

    [Fact]
    public void Dtls_profile_offer_with_spurious_crypto_is_answered_via_dtls_not_rejected()
    {
        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            DtlsAudioOfferWithSpuriousCrypto(), LocalEndPoint, [Pcmu], SdpMediaDirection.SendRecv, LocalDtlsOptions());

        // HARD-S1: was false before the fix (audio picked SDES on the DTLS profile then failed closed).
        Assert.True(result.Success);

        var audio = result.Answer!.Media[0];
        // Keyed via DTLS: fingerprint present, and the stray a=crypto is not echoed on the DTLS profile.
        Assert.NotNull(audio.Fingerprint);
        Assert.Empty(audio.Crypto);
    }

    [Fact]
    public void Dtls_profile_offer_with_spurious_crypto_fails_closed_without_local_dtls()
    {
        // No local DTLS identity: a=crypto is ignored on a DTLS profile, so the audio m-line cannot be
        // keyed and must fail closed rather than answer via the spurious SDES suite.
        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            DtlsAudioOfferWithSpuriousCrypto(), LocalEndPoint, [Pcmu], SdpMediaDirection.SendRecv, localOptions: null);

        Assert.False(result.Success);
    }
}
