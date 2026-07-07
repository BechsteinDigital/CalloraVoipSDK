using System;
using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound SDES/SRTP offer tests (RFC 4568 §6.1): the offerer now advertises RTP/SAVP with a
/// freshly generated <c>a=crypto</c> line and recovers both keys (local from its own offer,
/// remote from the answer) into <c>CallMediaParameters.SrtpKeys</c>. Also pins the plain-RTP
/// regression: without <c>OfferSdes</c> the offer stays RTP/AVP with no crypto.
/// </summary>
public sealed class SdesOfferTests
{
    private static readonly IPEndPoint OffererEndPoint = new(IPAddress.Parse("192.0.2.10"), 40000);
    private static readonly IPEndPoint AnswererEndPoint = new(IPAddress.Parse("192.0.2.20"), 50000);

    private static readonly SdpCodecDefinition[] Codecs =
    [
        new() { PayloadType = 8, Name = "PCMA", ClockRate = 8000 }
    ];

    // ── CreateOffer: profile + crypto emission ────────────────────────────────

    [Fact]
    public void CreateOffer_WithOfferSdes_EmitsSavpProfileAndCrypto()
    {
        var negotiator = new SdpOfferAnswerNegotiator();

        var offer = negotiator.CreateOffer(
            OffererEndPoint, Codecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { OfferSdes = true });

        var media = offer.Media[0];
        Assert.Equal("RTP/SAVP", media.Profile);

        var crypto = Assert.Single(media.Crypto);
        Assert.Equal(1, crypto.Tag);
        Assert.Equal("AES_CM_128_HMAC_SHA1_80", crypto.CryptoSuite);
        Assert.StartsWith("inline:", crypto.KeyParams);
        // AES-128 suite: 16-byte key + 14-byte salt = 30 raw bytes.
        Assert.Equal(30, DecodeInlineLength(crypto.KeyParams));
    }

    [Fact]
    public void CreateOffer_WithoutOfferSdes_StaysPlainRtpWithoutCrypto()
    {
        var negotiator = new SdpOfferAnswerNegotiator();

        var defaultOffer = negotiator.CreateOffer(
            OffererEndPoint, Codecs, SdpMediaDirection.SendRecv);
        Assert.Equal("RTP/AVP", defaultOffer.Media[0].Profile);
        Assert.Empty(defaultOffer.Media[0].Crypto);

        var explicitOffOffer = negotiator.CreateOffer(
            OffererEndPoint, Codecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { OfferSdes = false });
        Assert.Equal("RTP/AVP", explicitOffOffer.Media[0].Profile);
        Assert.Empty(explicitOffOffer.Media[0].Crypto);
    }

    [Fact]
    public void BuildDefaultSdp_PlainDial_ProducesNoCrypto()
    {
        // Regression: the default (Optional/Disabled) dial path must not emit SRTP keying.
        var plain = SdpUtilities.BuildDefaultSdp(OffererEndPoint, hold: false);
        Assert.Contains("RTP/AVP", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("a=crypto", plain, StringComparison.Ordinal);

        var withOptionsNoSdes = SdpUtilities.BuildDefaultSdp(
            OffererEndPoint, hold: false, new SdpMediaNegotiationOptions());
        Assert.Contains("RTP/AVP", withOptionsNoSdes, StringComparison.Ordinal);
        Assert.DoesNotContain("a=crypto", withOptionsNoSdes, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDefaultSdp_WithOfferSdes_ThroughUtilityChain_EmitsCrypto()
    {
        // Exercises the Application-port option and SdpUtilities.ConvertOptions mapping.
        var offerSdp = SdpUtilities.BuildDefaultSdp(
            OffererEndPoint, hold: false, new SdpMediaNegotiationOptions { OfferSdes = true });

        Assert.Contains("RTP/SAVP", offerSdp, StringComparison.Ordinal);
        Assert.Contains("a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:", offerSdp, StringComparison.Ordinal);
    }

    // ── End-to-end SDES symmetry (offerer role) ───────────────────────────────

    [Fact]
    public void SdesOffer_EndToEnd_OffererRecoversLocalAndRemoteKeys_Mirrored()
    {
        // Offerer emits an SDES offer through the full utility chain.
        var offerSdp = SdpUtilities.BuildDefaultSdp(
            OffererEndPoint, hold: false, new SdpMediaNegotiationOptions { OfferSdes = true });

        // Answerer accepts the suite and generates its own key (never echoes ours).
        var answerSdp = SdpUtilities.TryBuildNegotiatedAnswer(offerSdp, AnswererEndPoint, hold: false);
        Assert.False(string.IsNullOrWhiteSpace(answerSdp));
        Assert.Contains("a=crypto", answerSdp!, StringComparison.Ordinal);

        // Offerer role: remote SDP = the answer, local SDP = our own offer.
        var offererMp = SdpUtilities.TryParseMediaParameters(answerSdp!, OffererEndPoint, offerSdp);
        Assert.NotNull(offererMp);
        Assert.True(offererMp!.IsSrtpNegotiated);

        var offererKeys = offererMp.SrtpKeys;
        Assert.NotNull(offererKeys);
        Assert.Equal(SrtpCryptoSuiteKind.AesCm128HmacSha1_80, offererKeys!.Suite);

        // LocalMasterKey/Salt == the key we advertised in our own offer.
        var (offerKey, offerSalt) = DecodeInline(ParseFirstAudioCrypto(offerSdp).KeyParams, keyLength: 16);
        Assert.Equal(offerKey, offererKeys.LocalMasterKey.ToArray());
        Assert.Equal(offerSalt, offererKeys.LocalMasterSalt.ToArray());

        // RemoteMasterKey/Salt == the key carried in the peer's answer.
        var (answerKey, answerSalt) = DecodeInline(ParseFirstAudioCrypto(answerSdp!).KeyParams, keyLength: 16);
        Assert.Equal(answerKey, offererKeys.RemoteMasterKey.ToArray());
        Assert.Equal(answerSalt, offererKeys.RemoteMasterSalt.ToArray());

        // Answerer view is the mirror image: A.local == B.remote and A.remote == B.local.
        var answererMp = SdpUtilities.TryParseMediaParameters(offerSdp, AnswererEndPoint, answerSdp);
        Assert.NotNull(answererMp);
        var answererKeys = answererMp!.SrtpKeys;
        Assert.NotNull(answererKeys);

        Assert.Equal(offererKeys.LocalMasterKey.ToArray(), answererKeys!.RemoteMasterKey.ToArray());
        Assert.Equal(offererKeys.RemoteMasterKey.ToArray(), answererKeys.LocalMasterKey.ToArray());
        Assert.Equal(offererKeys.LocalMasterSalt.ToArray(), answererKeys.RemoteMasterSalt.ToArray());
        Assert.Equal(offererKeys.RemoteMasterSalt.ToArray(), answererKeys.LocalMasterSalt.ToArray());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SdpCryptoAttribute ParseFirstAudioCrypto(string sdp)
    {
        var parsed = new SdpSessionParser().Parse(sdp);
        var audio = parsed.Media.First(
            m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));
        return audio.Crypto[0];
    }

    private static (byte[] Key, byte[] Salt) DecodeInline(string keyParams, int keyLength)
    {
        const string prefix = "inline:";
        var b64 = keyParams[prefix.Length..].Split('|')[0];
        var raw = Convert.FromBase64String(b64);
        return (raw[..keyLength], raw[keyLength..]);
    }

    private static int DecodeInlineLength(string keyParams)
    {
        const string prefix = "inline:";
        var b64 = keyParams[prefix.Length..].Split('|')[0];
        return Convert.FromBase64String(b64).Length;
    }
}
