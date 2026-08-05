using System.Reflection;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-7 (config follow-up): the signaling service builds its own server-transaction engine, so the
/// server-transaction caps configured on the facade must be forwarded to that engine. This test pins the wire.
/// </summary>
public sealed class SipCallSignalingServiceTransactionCapWiringTests
{
    [Fact]
    public void Server_transaction_caps_are_forwarded_to_the_engine()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance,
            maxServerTransactions: 3,
            absoluteServerTransactionLifetime: TimeSpan.FromSeconds(42));

        var engine = (SipServerTransactionEngine)typeof(SipCallSignalingService)
            .GetField("_serverTransactions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

        var maxField = typeof(SipServerTransactionEngine)
            .GetField("_maxServerTransactions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var lifetimeField = typeof(SipServerTransactionEngine)
            .GetField("_absoluteTransactionLifetime", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.Equal(3, (int)maxField.GetValue(engine)!);
        Assert.Equal(TimeSpan.FromSeconds(42), (TimeSpan)lifetimeField.GetValue(engine)!);
    }
}
