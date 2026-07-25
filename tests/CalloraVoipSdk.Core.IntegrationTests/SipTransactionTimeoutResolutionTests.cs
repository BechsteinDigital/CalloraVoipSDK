using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [SIP] #13: transaction-timeout resolution must treat an explicitly set <c>Timeout</c> as an override even
/// when it equals the 64*T1 default, instead of silently re-deriving 64*T1 from a non-default T1. A null Timeout
/// derives the RFC 3261 64*T1 timeout.
/// </summary>
public sealed class SipTransactionTimeoutResolutionTests
{
    private static SipClientTransactionRequest Request(TimeSpan? timeout, TimeSpan t1) => new()
    {
        Method = "INVITE",
        RequestUri = "sip:x@h.invalid",
        Headers = new Dictionary<string, string>(),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5060),
        Timeout = timeout,
        T1 = t1,
    };

    [Fact]
    public void An_explicit_32s_timeout_is_honoured_even_with_a_non_default_T1()
    {
        // Regression guard: the old heuristic (Timeout != 32s ? explicit : derive) re-derived 64*T1 = 16s here,
        // discarding the caller's explicit 32 s. Now the explicit value wins.
        var resolved = SipClientTransactionExecutor.ResolveTransactionTimeout(
            Request(TimeSpan.FromSeconds(32), TimeSpan.FromMilliseconds(250)));

        Assert.Equal(TimeSpan.FromSeconds(32), resolved);
    }

    [Fact]
    public void An_explicit_non_default_timeout_is_honoured()
    {
        var resolved = SipClientTransactionExecutor.ResolveTransactionTimeout(
            Request(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500)));

        Assert.Equal(TimeSpan.FromSeconds(10), resolved);
    }

    [Fact]
    public void A_null_timeout_derives_64xT1()
    {
        var resolved = SipClientTransactionExecutor.ResolveTransactionTimeout(
            Request(timeout: null, TimeSpan.FromMilliseconds(500)));

        Assert.Equal(TimeSpan.FromSeconds(32), resolved);
    }

    [Fact]
    public void A_null_timeout_with_a_custom_T1_derives_the_scaled_timeout()
    {
        var resolved = SipClientTransactionExecutor.ResolveTransactionTimeout(
            Request(timeout: null, TimeSpan.FromMilliseconds(250)));

        Assert.Equal(TimeSpan.FromSeconds(16), resolved);
    }
}
