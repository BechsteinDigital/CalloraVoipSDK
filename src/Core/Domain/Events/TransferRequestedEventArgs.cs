using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for the call <c>TransferRequested</c> event (an inbound SIP REFER).</summary>
public sealed class TransferRequestedEventArgs : EventArgs
{
    /// <summary>The transfer target URI requested by the peer.</summary>
    public string TargetUri { get; }

    /// <summary>The call the peer asked to transfer.</summary>
    public ICall  Call      { get; }
    /// <summary>Set to <c>true</c> in the event handler to accept the transfer.</summary>
    public bool   Accept    { get; set; }

    /// <summary>
    /// Live handle to the REFER's implicit subscription (RFC 3515 / RFC 6665). After accepting, the
    /// application places the referred call itself and reports its progress and final outcome through
    /// this handle so the transferor sees the real status. Reporting is optional.
    /// </summary>
    public IReferSubscription Subscription { get; }

    internal TransferRequestedEventArgs(string targetUri, ICall call, IReferSubscription subscription)
        => (TargetUri, Call, Subscription) = (targetUri, call, subscription);
}
