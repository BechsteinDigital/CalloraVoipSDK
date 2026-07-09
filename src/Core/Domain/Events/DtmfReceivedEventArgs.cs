using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for the call <c>DtmfReceived</c> event.</summary>
public sealed class DtmfReceivedEventArgs : EventArgs
{
    /// <summary>The DTMF tone that was received.</summary>
    public DtmfTone Tone       { get; }

    /// <summary>Reported tone duration in milliseconds.</summary>
    public int      DurationMs { get; }

    /// <summary>The call on which the tone was received.</summary>
    public ICall    Call       { get; }

    internal DtmfReceivedEventArgs(DtmfTone tone, int durationMs, ICall call)
        => (Tone, DurationMs, Call) = (tone, durationMs, call);
}
