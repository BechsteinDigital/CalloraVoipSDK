using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDES answer negotiation (RFC 4568 §5.1.2/§5.1.3, package S1): the answer must carry
/// our OWN freshly generated master key — never echo the offerer's key — mirror the tag
/// of the chosen crypto line, reject keyless SAVP, and leave plain RTP/AVP untouched.
/// </summary>
public sealed class SdpSdesAnswerTests
{
    private const string OfferedKeyParams = "inline:WVNfX19TZWNyZXRLZXlfMTZCX1NhbHRfMTRCeXQ=";

    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Parse("192.0.2.10"), 40000);

    private static readonly SdpCodecDefinition Pcmu = new()
    {
        PayloadType = 0,
        Name = "PCMU",
        ClockRate = 8000
    };

    private static SdpSessionDescription Offer(
        string profile,
        params SdpCryptoAttribute[] crypto) => new()
    {
        OriginAddress = "203.0.113.5",
        ConnectionAddress = "203.0.113.5",
        Media =
        [
            new SdpMediaDescription
            {
                MediaType = "audio",
                Port = 20000,
                Profile = profile,
                Codecs = [Pcmu],
                Direction = SdpMediaDirection.SendRecv,
                Crypto = crypto
            }
        ]
    };

    private static SdpCryptoAttribute CryptoLine(int tag, string suite, string keyParams = OfferedKeyParams) => new()
    {
        Tag = tag,
        CryptoSuite = suite,
        KeyParams = keyParams
    };

    private static SdpOfferAnswerResult Negotiate(SdpSessionDescription offer) =>
        new SdpOfferAnswerNegotiator().NegotiateAnswer(offer, LocalEndPoint, [Pcmu], SdpMediaDirection.SendRecv);

    [Fact]
    public void Savp_answer_generates_own_key_and_never_echoes_the_offered_key()
    {
        var result = Negotiate(Offer("RTP/SAVP", CryptoLine(1, "AES_CM_128_HMAC_SHA1_80")));

        Assert.True(result.Success);
        var answerCrypto = Assert.Single(result.Answer!.Media[0].Crypto);
        Assert.NotEqual(OfferedKeyParams, answerCrypto.KeyParams);
        Assert.StartsWith("inline:", answerCrypto.KeyParams);

        // AES-128 suite: base64 of exactly 16-byte key + 14-byte salt (RFC 4568 §6.1).
        var raw = Convert.FromBase64String(answerCrypto.KeyParams["inline:".Length..]);
        Assert.Equal(30, raw.Length);
    }

    [Fact]
    public void Answer_mirrors_tag_and_suite_of_the_chosen_offer_line()
    {
        var result = Negotiate(Offer(
            "RTP/SAVP",
            CryptoLine(3, "F8_128_HMAC_SHA1_80"),
            CryptoLine(7, "AES_CM_128_HMAC_SHA1_32")));

        Assert.True(result.Success);
        var answerCrypto = Assert.Single(result.Answer!.Media[0].Crypto);
        Assert.Equal(7, answerCrypto.Tag);
        Assert.Equal("AES_CM_128_HMAC_SHA1_32", answerCrypto.CryptoSuite);
    }

    [Fact]
    public void Result_exposes_remote_and_local_crypto_separately()
    {
        var result = Negotiate(Offer("RTP/SAVP", CryptoLine(1, "AES_CM_128_HMAC_SHA1_80")));

        Assert.NotNull(result.NegotiatedCrypto);
        Assert.NotNull(result.LocalCrypto);
        Assert.Equal(OfferedKeyParams, result.NegotiatedCrypto!.KeyParams);
        Assert.NotEqual(result.NegotiatedCrypto.KeyParams, result.LocalCrypto!.KeyParams);
    }

    [Fact]
    public void Two_negotiations_generate_different_local_keys()
    {
        var first = Negotiate(Offer("RTP/SAVP", CryptoLine(1, "AES_CM_128_HMAC_SHA1_80")));
        var second = Negotiate(Offer("RTP/SAVP", CryptoLine(1, "AES_CM_128_HMAC_SHA1_80")));

        Assert.NotEqual(first.LocalCrypto!.KeyParams, second.LocalCrypto!.KeyParams);
    }

    [Fact]
    public void Savp_offer_with_only_unsupported_suites_is_rejected()
    {
        var result = Negotiate(Offer("RTP/SAVP", CryptoLine(1, "F8_128_HMAC_SHA1_80")));

        Assert.False(result.Success);
    }

    [Fact]
    public void Savp_offer_without_any_crypto_line_is_rejected()
    {
        var result = Negotiate(Offer("RTP/SAVP"));

        Assert.False(result.Success);
    }

    [Fact]
    public void Avp_offer_with_unsupported_crypto_falls_back_to_plain_rtp()
    {
        var result = Negotiate(Offer("RTP/AVP", CryptoLine(1, "F8_128_HMAC_SHA1_80")));

        Assert.True(result.Success);
        Assert.Empty(result.Answer!.Media[0].Crypto);
        Assert.Null(result.LocalCrypto);
        Assert.Equal("RTP/AVP", result.Answer.Media[0].Profile);
    }

    [Fact]
    public void Plain_avp_offer_stays_untouched()
    {
        // sipgate pattern from the live trunk: RTP/AVP, no crypto.
        var result = Negotiate(Offer("RTP/AVP"));

        Assert.True(result.Success);
        Assert.Empty(result.Answer!.Media[0].Crypto);
        Assert.Null(result.NegotiatedCrypto);
        Assert.Null(result.LocalCrypto);
        Assert.Equal("RTP/AVP", result.Answer.Media[0].Profile);
    }

    [Fact]
    public void Aes256_suite_generates_46_byte_key_material()
    {
        var result = Negotiate(Offer("RTP/SAVP", CryptoLine(1, "AES_256_CM_HMAC_SHA1_80",
            $"inline:{Convert.ToBase64String(new byte[46])}")));

        Assert.True(result.Success);
        var raw = Convert.FromBase64String(
            result.LocalCrypto!.KeyParams["inline:".Length..]);
        Assert.Equal(46, raw.Length);
    }

    [Fact]
    public void Non_inline_key_params_are_skipped()
    {
        var result = Negotiate(Offer(
            "RTP/SAVP",
            CryptoLine(1, "AES_CM_128_HMAC_SHA1_80", "uri:key-server-not-supported"),
            CryptoLine(2, "AES_CM_128_HMAC_SHA1_80")));

        Assert.True(result.Success);
        Assert.Equal(2, Assert.Single(result.Answer!.Media[0].Crypto).Tag);
    }
}
