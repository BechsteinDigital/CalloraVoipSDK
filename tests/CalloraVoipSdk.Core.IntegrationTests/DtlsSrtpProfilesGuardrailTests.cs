using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The DTLS-SRTP protection-profile policy (RFC 5764 §4.1.2): AEAD-GCM (RFC 7714) is offered and accepted,
/// preferred over the classic AES-CM+HMAC suites, so the SDK negotiates GCM with peers that prefer it
/// (Firefox, current SIPSorcery) while still interoperating with AES-CM-only peers.
/// </summary>
public sealed class DtlsSrtpProfilesGuardrailTests
{
    private const int SrtpAeadAes128Gcm = 0x0007;
    private const int SrtpAeadAes256Gcm = 0x0008;

    [Fact]
    public void Aead_gcm_is_offered_and_preferred_over_aes_cm()
    {
        // GCM-128 leads, then GCM-256, then the AES-CM fallbacks.
        Assert.Equal(
            [SrtpAeadAes128Gcm,
             SrtpAeadAes256Gcm,
             SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80,
             SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_32],
            DtlsSrtpProfiles.Supported);
    }

    [Fact]
    public void SelectFromOffered_picks_gcm_when_the_peer_offers_both()
    {
        // A browser offering AES-CM and GCM negotiates GCM-128 — our top preference.
        Assert.Equal(SrtpAeadAes128Gcm, DtlsSrtpProfiles.SelectFromOffered(
            [SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80, SrtpAeadAes128Gcm, SrtpAeadAes256Gcm]));
    }

    [Fact]
    public void SelectFromOffered_falls_back_to_aes_cm_for_an_aes_cm_only_peer()
    {
        Assert.Equal(SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80,
            DtlsSrtpProfiles.SelectFromOffered([SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80]));
    }

    [Fact]
    public void SelectFromOffered_returns_null_and_the_error_names_both_families_for_an_exotic_offer()
    {
        Assert.Null(DtlsSrtpProfiles.SelectFromOffered([0x0005])); // NULL_HMAC_SHA1_80 — unsupported

        var message = DtlsSrtpProfiles.FormatNoCommonProfileError([0x0005]);
        Assert.Contains("AEAD-GCM", message, StringComparison.Ordinal);
        Assert.Contains("AES-CM-128", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCryptoSuite_maps_the_gcm_profiles()
    {
        Assert.Equal(SrtpCryptoSuite.AeadAes128Gcm, DtlsSrtpProfiles.ToCryptoSuite(SrtpAeadAes128Gcm));
        Assert.Equal(SrtpCryptoSuite.AeadAes256Gcm, DtlsSrtpProfiles.ToCryptoSuite(SrtpAeadAes256Gcm));
    }
}
