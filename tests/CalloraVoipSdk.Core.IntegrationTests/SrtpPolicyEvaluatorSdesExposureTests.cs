using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Domain.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RFC 4568 §7 exposure rule: SDES places the SRTP master key in the SDP, so it is only
/// confidential when the signaling transport is (TLS/SIPS). DTLS-SRTP keys never appear in the SDP,
/// and a Disabled policy offers no key at all — both are exempt.
/// </summary>
public sealed class SrtpPolicyEvaluatorSdesExposureTests
{
    [Theory]
    // policy, offerDtlsSrtp, signalingIsSecure, expectedExposed
    [InlineData(SrtpPolicy.Optional, false, false, true)]   // SDES over UDP → master key in cleartext SDP
    [InlineData(SrtpPolicy.Required, false, false, true)]   // "Required" over UDP still leaks the key
    [InlineData(SrtpPolicy.Optional, false, true, false)]   // SDES over TLS → key protected
    [InlineData(SrtpPolicy.Required, false, true, false)]   // SDES over TLS → key protected
    [InlineData(SrtpPolicy.Optional, true, false, false)]   // DTLS-SRTP → keys never in the SDP
    [InlineData(SrtpPolicy.Required, true, false, false)]   // DTLS-SRTP → keys never in the SDP
    [InlineData(SrtpPolicy.Disabled, false, false, false)]  // no SRTP offered → no key at all
    public void ExposesSdesKeyOverInsecureSignaling_matrix(
        SrtpPolicy policy, bool offerDtlsSrtp, bool signalingIsSecure, bool expected)
    {
        Assert.Equal(
            expected,
            SrtpPolicyEvaluator.ExposesSdesKeyOverInsecureSignaling(policy, offerDtlsSrtp, signalingIsSecure));
    }
}
