using System;
using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDES (RFC 4568) key negotiation and plumbing tests: suite mapping, the answerer echo-bug
/// regression, and SrtpKeys propagation into CallMediaParameters (SRTP wiring step 1).
/// </summary>
public sealed class SdesNegotiationTests
{
    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Parse("192.0.2.20"), 50000);
    private static readonly SdpCodecDefinition[] LocalCaps =
    [
        new() { PayloadType = 8, Name = "PCMA", ClockRate = 8000 }
    ];

    // ── Suite mapping ─────────────────────────────────────────────────────────

    // Signatures use only the public domain Kind (the infrastructure SrtpCryptoSuite enum is
    // internal), exercising the full String -> SrtpCryptoSuite -> SrtpCryptoSuiteKind chain.
    [Theory]
    [InlineData("AES_CM_128_HMAC_SHA1_80", SrtpCryptoSuiteKind.AesCm128HmacSha1_80, 16)]
    [InlineData("AES_CM_128_HMAC_SHA1_32", SrtpCryptoSuiteKind.AesCm128HmacSha1_32, 16)]
    [InlineData("AES_256_CM_HMAC_SHA1_80", SrtpCryptoSuiteKind.AesCm256HmacSha1_80, 32)]
    [InlineData("AES_256_CM_HMAC_SHA1_32", SrtpCryptoSuiteKind.AesCm256HmacSha1_32, 32)]
    [InlineData("AES_CM_256_HMAC_SHA1_80", SrtpCryptoSuiteKind.AesCm256HmacSha1_80, 32)]
    [InlineData("aes_cm_128_hmac_sha1_80", SrtpCryptoSuiteKind.AesCm128HmacSha1_80, 16)]
    public void SuiteMapper_MapsNameToKindAndKeyLength(
        string name, SrtpCryptoSuiteKind expectedKind, int expectedKeyLength)
    {
        Assert.True(SrtpCryptoSuiteMapper.TryParseSuiteName(name, out var suite));
        Assert.Equal(expectedKind, SrtpCryptoSuiteMapper.ToDomainKind(suite));
        Assert.Equal(expectedKeyLength, SrtpCryptoSuiteMapper.GetMasterKeyLength(suite));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("AES_GCM_128")]
    [InlineData("F8_128_HMAC_SHA1_80")]
    public void TryParseSuiteName_ReturnsFalse_ForUnknownOrEmpty(string? name)
    {
        Assert.False(SrtpCryptoSuiteMapper.TryParseSuiteName(name, out _));
    }

    // ── Negotiator: echo-bug regression + directionality ──────────────────────

    [Fact]
    public void NegotiateAnswer_GeneratesOwnLocalKey_NeverEchoesOffer()
    {
        var offerKeyMaterial = BuildKeyMaterial(seed: 1, length: 30);
        var offerCrypto = BuildCrypto("AES_CM_128_HMAC_SHA1_80", offerKeyMaterial);
        var offer = BuildOffer("RTP/SAVP", offerCrypto);

        var negotiator = new SdpOfferAnswerNegotiator();
        var result = negotiator.NegotiateAnswer(
            offer, LocalEndPoint, LocalCaps, SdpMediaDirection.SendRecv);

        Assert.True(result.Success);
        var answerCrypto = Assert.Single(result.Answer!.Media[0].Crypto);

        // Echo-bug regression: the answer's inline key must NOT equal the offer's.
        Assert.NotEqual(offerCrypto.KeyParams, answerCrypto.KeyParams);

        // Remote = accepted offer crypto; Local = the freshly generated answer crypto.
        Assert.Equal(offerCrypto.KeyParams, result.NegotiatedCrypto!.KeyParams);
        Assert.Equal(answerCrypto.KeyParams, result.LocalCrypto!.KeyParams);

        // Same tag + suite name as accepted (RFC 4568 §6.1).
        Assert.Equal(offerCrypto.Tag, answerCrypto.Tag);
        Assert.Equal(offerCrypto.CryptoSuite, answerCrypto.CryptoSuite);

        // 128-suite: 16-byte key + 14-byte salt = 30 raw bytes.
        Assert.Equal(30, DecodeInlineLength(answerCrypto.KeyParams));
    }

    [Fact]
    public void NegotiateAnswer_Generates256BitLocalKey_WithCorrectLength()
    {
        var offerCrypto = BuildCrypto("AES_256_CM_HMAC_SHA1_80", BuildKeyMaterial(seed: 7, length: 46));
        var offer = BuildOffer("RTP/SAVP", offerCrypto);

        var negotiator = new SdpOfferAnswerNegotiator();
        var result = negotiator.NegotiateAnswer(
            offer, LocalEndPoint, LocalCaps, SdpMediaDirection.SendRecv);

        var answerCrypto = Assert.Single(result.Answer!.Media[0].Crypto);
        // 256-suite: 32-byte key + 14-byte salt = 46 raw bytes.
        Assert.Equal(46, DecodeInlineLength(answerCrypto.KeyParams));
    }

    [Fact]
    public void NegotiateAnswer_NoCrypto_ForPlainRtpOffer()
    {
        var offer = BuildOffer("RTP/AVP", crypto: null);

        var negotiator = new SdpOfferAnswerNegotiator();
        var result = negotiator.NegotiateAnswer(
            offer, LocalEndPoint, LocalCaps, SdpMediaDirection.SendRecv);

        Assert.True(result.Success);
        Assert.Empty(result.Answer!.Media[0].Crypto);
        Assert.Null(result.NegotiatedCrypto);
        Assert.Null(result.LocalCrypto);
    }

    // ── End-to-end plumbing into CallMediaParameters ──────────────────────────

    [Fact]
    public void TryParseMediaParameters_CarriesSrtpKeys_AfterSdesNegotiation()
    {
        var offerKeyMaterial = BuildKeyMaterial(seed: 3, length: 30);
        var offerCrypto = BuildCrypto("AES_CM_128_HMAC_SHA1_80", offerKeyMaterial);
        var offerSdp = Serialize(BuildOffer("RTP/SAVP", offerCrypto));

        // Build the answer we would send (contains our own local a=crypto).
        var answerSdp = SdpUtilities.TryBuildNegotiatedAnswer(offerSdp, LocalEndPoint, hold: false);
        Assert.False(string.IsNullOrWhiteSpace(answerSdp));

        var mp = SdpUtilities.TryParseMediaParameters(offerSdp, LocalEndPoint, answerSdp);

        Assert.NotNull(mp);
        var keys = mp!.SrtpKeys;
        Assert.NotNull(keys);
        Assert.Equal(SrtpCryptoSuiteKind.AesCm128HmacSha1_80, keys!.Suite);

        // Remote direction = the offer's key material (we receive with it).
        Assert.Equal(offerKeyMaterial[..16], keys.RemoteMasterKey.ToArray());
        Assert.Equal(offerKeyMaterial[16..30], keys.RemoteMasterSalt.ToArray());

        // Local direction = our own generated key (we send with it): correct length, distinct.
        Assert.Equal(16, keys.LocalMasterKey.Length);
        Assert.Equal(14, keys.LocalMasterSalt.Length);
        Assert.False(keys.LocalMasterKey.ToArray().SequenceEqual(keys.RemoteMasterKey.ToArray()));
    }

    [Fact]
    public void TryParseMediaParameters_SrtpKeysNull_ForPlainRtp()
    {
        var offerSdp = Serialize(BuildOffer("RTP/AVP", crypto: null));
        var answerSdp = SdpUtilities.TryBuildNegotiatedAnswer(offerSdp, LocalEndPoint, hold: false);

        var mp = SdpUtilities.TryParseMediaParameters(offerSdp, LocalEndPoint, answerSdp);

        Assert.NotNull(mp);
        Assert.Null(mp!.SrtpKeys);
    }

    [Fact]
    public void TryParseMediaParameters_SrtpKeysNull_WhenLocalSdpMissing()
    {
        // Remote offered SDES but we pass no local SDP: only one direction is known, so no keys.
        var offerCrypto = BuildCrypto("AES_CM_128_HMAC_SHA1_80", BuildKeyMaterial(seed: 5, length: 30));
        var offerSdp = Serialize(BuildOffer("RTP/SAVP", offerCrypto));

        var mp = SdpUtilities.TryParseMediaParameters(offerSdp, LocalEndPoint, localSdp: null);

        Assert.NotNull(mp);
        Assert.Null(mp!.SrtpKeys);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildKeyMaterial(byte seed, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(seed + i);
        return bytes;
    }

    private static SdpCryptoAttribute BuildCrypto(string suite, byte[] keyMaterial) => new()
    {
        Tag = 1,
        CryptoSuite = suite,
        KeyParams = "inline:" + Convert.ToBase64String(keyMaterial)
    };

    private static SdpSessionDescription BuildOffer(string profile, SdpCryptoAttribute? crypto) => new()
    {
        OriginAddress = "192.0.2.10",
        ConnectionAddress = "192.0.2.10",
        SessionDirection = SdpMediaDirection.SendRecv,
        Media =
        [
            new SdpMediaDescription
            {
                MediaType = "audio",
                Port = 40000,
                Profile = profile,
                Direction = SdpMediaDirection.SendRecv,
                Codecs = [new SdpCodecDefinition { PayloadType = 8, Name = "PCMA", ClockRate = 8000 }],
                Crypto = crypto is null ? [] : [crypto]
            }
        ]
    };

    private static string Serialize(SdpSessionDescription session)
        => new SdpSessionSerializer().Serialize(session);

    private static int DecodeInlineLength(string keyParams)
    {
        const string prefix = "inline:";
        var b64 = keyParams[prefix.Length..].Split('|')[0];
        return Convert.FromBase64String(b64).Length;
    }
}
