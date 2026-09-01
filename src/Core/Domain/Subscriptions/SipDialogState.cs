namespace CalloraVoipSdk.Core.Domain.Subscriptions;

/// <summary>The state of one dialog in a dialog-info document (RFC 4235 §3.7.1).</summary>
public enum SipDialogState
{
    /// <summary>A value this SDK does not recognise. Read as "we do not know", never as idle.</summary>
    Unknown = 0,

    /// <summary>An INVITE has been sent and nothing has come back yet.</summary>
    Trying = 1,

    /// <summary>A non-ringing provisional response arrived.</summary>
    Proceeding = 2,

    /// <summary>It is ringing. This is the state a pick-up key acts on.</summary>
    Early = 3,

    /// <summary>The call is up. This is the state a busy lamp lights on.</summary>
    Confirmed = 4,

    /// <summary>The dialog is over.</summary>
    Terminated = 5
}
