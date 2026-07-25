using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Event payload for inbound REFER transfer requests.
/// </summary>
internal sealed class SipTransferRequestedEventArgs : EventArgs
{
    /// <summary>
    /// Creates transfer-request event payload.
    /// </summary>
    public SipTransferRequestedEventArgs(string referTo, string referredBy, IReferSubscription subscription)
    {
        ReferTo = referTo;
        ReferredBy = referredBy;
        Subscription = subscription;
    }

    /// <summary>
    /// Target URI requested by remote REFER.
    /// </summary>
    public string ReferTo { get; }

    /// <summary>
    /// Identity string for transfer initiator.
    /// </summary>
    public string ReferredBy { get; }

    /// <summary>
    /// Live handle to the REFER's implicit subscription, surfaced to the SDK consumer for progress reporting.
    /// </summary>
    public IReferSubscription Subscription { get; }

    /// <summary>
    /// Application decision whether transfer request should be accepted.
    /// </summary>
    public bool Accept { get; set; }
}

