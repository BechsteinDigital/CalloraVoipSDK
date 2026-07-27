namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>All domain-level callbacks from the transport layer into the Call aggregate.</summary>
internal sealed record CallChannelCallbacks(
    Action<CallState, CallTerminationReason?>    OnStateChange,
    Action<byte, int>?                           OnDtmf              = null,
    Action<bool>?                                OnRemoteHold        = null,
    Func<string, string, IReferSubscription, bool>? OnTransferRequested = null);
