using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [SIP] #13: UAC response-to-transaction matching must follow RFC 3261 §17.1.3 — the response's top-Via
/// branch must be present AND equal to the request's branch. A response missing the branch was previously
/// accepted (too loose) and is now rejected.
/// </summary>
public sealed class SipClientTransactionMatchingTests
{
    private const string Branch = "z9hG4bK-abc123";

    private static SipResponse Response(string via) => new(
        200,
        "OK",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = via,
            ["Call-ID"] = "call-1",
            ["CSeq"] = "1 INVITE",
        },
        body: string.Empty);

    [Fact]
    public void A_response_with_the_matching_branch_matches()
    {
        Assert.True(SipClientTransactionExecutor.MatchesTransaction(
            Response($"SIP/2.0/UDP host.invalid;branch={Branch}"), "call-1", 1, "INVITE", Branch));
    }

    [Fact]
    public void A_response_without_a_via_branch_does_not_match()
    {
        // Regression guard: previously returned true (matched) despite the missing branch.
        Assert.False(SipClientTransactionExecutor.MatchesTransaction(
            Response("SIP/2.0/UDP host.invalid"), "call-1", 1, "INVITE", Branch));
    }

    [Fact]
    public void A_response_with_a_different_branch_does_not_match()
    {
        Assert.False(SipClientTransactionExecutor.MatchesTransaction(
            Response("SIP/2.0/UDP host.invalid;branch=z9hG4bK-other"), "call-1", 1, "INVITE", Branch));
    }
}
