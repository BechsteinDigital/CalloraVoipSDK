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

    internal TransferRequestedEventArgs(string targetUri, ICall call)
        => (TargetUri, Call) = (targetUri, call);
}
