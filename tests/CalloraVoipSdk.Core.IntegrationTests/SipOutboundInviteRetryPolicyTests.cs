using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [SIP] #13: the outbound-INVITE transport-failure classifier must recognise only a genuine transaction
/// transport failure (<see cref="SipTransactionTransportException"/>) for candidate failover / synthetic 503,
/// not any <see cref="InvalidOperationException"/> that merely carries an inner exception (e.g. a failed PRACK or
/// a negotiation error) — those must propagate unchanged instead of being masked as a transport failure.
/// </summary>
public sealed class SipOutboundInviteRetryPolicyTests
{
    [Fact]
    public void A_transaction_transport_exception_is_classified_as_transport_failure()
    {
        var transport = new SipTransactionTransportException(
            "SIP transaction send failed for INVITE.", new System.Net.Sockets.SocketException());

        Assert.True(SipOutboundInviteRetryPolicy.IsTransportFailure(transport));
    }

    [Fact]
    public void A_non_transport_invalid_operation_with_an_inner_is_not_a_transport_failure()
    {
        // Regression guard: the old heuristic (InnerException is not null) misclassified this as a transport
        // failure, triggering wrongful failover and a synthetic 503.
        var nonTransport = new InvalidOperationException(
            "PRACK negotiation failed.", new InvalidOperationException("inner cause"));

        Assert.False(SipOutboundInviteRetryPolicy.IsTransportFailure(nonTransport));
    }

    [Fact]
    public void A_plain_invalid_operation_without_an_inner_is_not_a_transport_failure()
    {
        Assert.False(SipOutboundInviteRetryPolicy.IsTransportFailure(
            new InvalidOperationException("Dialog must be Idle.")));
    }
}
