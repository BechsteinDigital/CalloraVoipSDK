using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for the line <c>StateChanged</c> event.</summary>
public sealed class LineStateChangedEventArgs : EventArgs
{
    /// <summary>The registration state the line transitioned from.</summary>
    public LineState  OldState { get; }

    /// <summary>The registration state the line transitioned to.</summary>
    public LineState  NewState { get; }

    /// <summary>The line whose state changed.</summary>
    public IPhoneLine Line     { get; }

    internal LineStateChangedEventArgs(LineState old, LineState next, IPhoneLine line)
        => (OldState, NewState, Line) = (old, next, line);
}
