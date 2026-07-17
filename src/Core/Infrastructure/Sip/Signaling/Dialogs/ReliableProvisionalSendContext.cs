using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Bundles the per-session dependencies and timing settings the reliable-provisional 100rel/PRACK
/// send flow (RFC 3262) needs, so
/// <see cref="SipCallSessionUtilities.SendReliableProvisionalAndWaitForPrackAsync"/> takes the two
/// call inputs plus one context instead of a twelve-argument list (HARD-R7). Owned and populated by
/// the calling <c>SipCallSession</c>; carries no behaviour of its own.
/// </summary>
internal sealed class ReliableProvisionalSendContext
{
    /// <summary>The dialog's Call-ID, used only for diagnostics.</summary>
    public required string CallId { get; init; }

    /// <summary>Tracks the pending provisional and awaits its PRACK.</summary>
    public required SipReliableProvisionalManager ReliableProvisionalManager { get; init; }

    /// <summary>Builds the provisional/timeout response headers from the INVITE.</summary>
    public required SipCallSessionHeaderService HeaderService { get; init; }

    /// <summary>Sends the 180/504 responses on the server transaction.</summary>
    public required ISipServerTransactionEngine ServerTransactions { get; init; }

    /// <summary>Destination for the provisional responses.</summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    /// <summary>Transport the dialog signals over — drives the UDP retransmit loop.</summary>
    public required SipTransportProtocol SignalingTransport { get; init; }

    /// <summary>Logger for the handshake diagnostics.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Overall PRACK wait timeout.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>Initial provisional retransmit interval (RFC 3262 Timer, seeded from T1).</summary>
    public required TimeSpan ReliableProvisionalT1 { get; init; }

    /// <summary>Upper bound for the exponential provisional retransmit backoff (T2).</summary>
    public required TimeSpan ReliableProvisionalT2 { get; init; }
}
