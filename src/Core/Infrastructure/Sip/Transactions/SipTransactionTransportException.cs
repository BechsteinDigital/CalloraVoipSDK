namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;

/// <summary>
/// Raised when a SIP client transaction fails at the transport layer — the retransmission/send loop faulted
/// (e.g. a <see cref="System.Net.Sockets.SocketException"/> or connection error) rather than the transaction
/// receiving a SIP final response. Distinguishes a genuine transport failure (which warrants candidate failover
/// and a synthetic 503, RFC 3261 §18.4) from any other <see cref="InvalidOperationException"/> that merely
/// happens to carry an inner exception (e.g. a state or negotiation error), which must not be misclassified.
/// The originating transport error is preserved as <see cref="System.Exception.InnerException"/>.
/// </summary>
internal sealed class SipTransactionTransportException : InvalidOperationException
{
    /// <summary>Creates the exception wrapping the originating transport failure.</summary>
    public SipTransactionTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
