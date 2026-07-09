namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>Lifecycle state of a call.</summary>
public enum CallState
{
    /// <summary>No call activity; the initial state before dialing or ringing.</summary>
    Idle,

    /// <summary>Outbound call: the INVITE has been sent and the SDK is awaiting a response.</summary>
    Dialing,

    /// <summary>The call is ringing — outbound (remote alerting) or inbound (awaiting local accept/reject).</summary>
    Ringing,

    /// <summary>The call is answered and media is (or is about to be) flowing.</summary>
    Connected,

    /// <summary>The call is on hold; media is suspended in at least one direction.</summary>
    OnHold,

    /// <summary>A transfer (blind or attended) is in progress for this call.</summary>
    Transferring,

    /// <summary>The call has ended (hung up, rejected, or failed); a terminal state.</summary>
    Terminated
}
