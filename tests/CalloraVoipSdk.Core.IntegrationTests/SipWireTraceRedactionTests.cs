using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Trace wire logs must not leak the SDES SRTP key material or ICE passwords that SDP carries
/// in the clear, while keeping non-secret lines for diagnostics.
/// </summary>
public class SipWireTraceRedactionTests
{
    [Fact]
    public void Redacts_sdes_crypto_inline_key()
    {
        const string key = "PS1uQCVeeCFCanVmcjkpPywjNWhcYD0mXXtxaVBR";
        var sdp =
            "v=0\r\n" +
            "m=audio 5004 RTP/SAVP 0\r\n" +
            $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:{key}|2^20|1:32\r\n";

        var redacted = SipWireTraceLogger.RedactSensitiveSdp(sdp);

        Assert.DoesNotContain(key, redacted);
        Assert.Contains("inline:<redacted>", redacted);
        Assert.Contains("AES_CM_128_HMAC_SHA1_80", redacted); // suite preserved, only the key masked
    }

    [Fact]
    public void Redacts_ice_password_but_keeps_ufrag()
    {
        var sdp = "a=ice-ufrag:abcd\r\na=ice-pwd:SuperSecretIcePassword12345\r\n";

        var redacted = SipWireTraceLogger.RedactSensitiveSdp(sdp);

        Assert.DoesNotContain("SuperSecretIcePassword12345", redacted);
        Assert.Contains("a=ice-pwd:<redacted>", redacted);
        Assert.Contains("a=ice-ufrag:abcd", redacted);
    }

    [Fact]
    public void Leaves_plain_sdp_untouched()
    {
        var sdp = "v=0\r\nm=audio 5004 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

        Assert.Equal(sdp, SipWireTraceLogger.RedactSensitiveSdp(sdp));
    }
}
