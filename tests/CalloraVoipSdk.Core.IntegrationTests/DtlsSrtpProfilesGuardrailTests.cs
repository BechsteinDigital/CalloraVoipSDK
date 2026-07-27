using CalloraVoipSdk.Core.Infrastructure.Dtls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// GA guardrail on the DTLS-SRTP profile negotiation: when no common protection profile exists the failure
/// diagnoses the cause instead of surfacing an anonymous <c>insufficient_security</c> alert — in particular the
/// common Firefox-only-GCM interop failure (peer offers only AEAD-GCM, RFC 7714, which the SDK does not implement).
/// </summary>
public sealed class DtlsSrtpProfilesGuardrailTests
{
    [Fact]
    public void No_common_profile_error_names_gcm_when_the_peer_offered_only_gcm()
    {
        var message = DtlsSrtpProfiles.FormatNoCommonProfileError([0x0007, 0x0008]); // AEAD_AES_128/256_GCM

        Assert.Contains("AEAD-GCM", message, StringComparison.Ordinal);
        Assert.Contains("AES-CM-128", message, StringComparison.Ordinal);
        Assert.Contains("0x0007", message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_common_profile_error_is_generic_for_a_non_gcm_offer()
    {
        var message = DtlsSrtpProfiles.FormatNoCommonProfileError([0x0005]); // NULL_HMAC_SHA1_80 — unsupported, not GCM

        Assert.DoesNotContain("AEAD-GCM", message, StringComparison.Ordinal);
        Assert.Contains("AES-CM-128", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_supported_profiles_are_aes_cm_128_only()
    {
        Assert.Equal(
            [Org.BouncyCastle.Tls.SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80,
             Org.BouncyCastle.Tls.SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_32],
            DtlsSrtpProfiles.Supported);
        Assert.Null(DtlsSrtpProfiles.SelectFromOffered([0x0007, 0x0008])); // no overlap with GCM
    }
}
