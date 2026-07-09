using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Events;

/// <summary>Payload for a call-related error notification.</summary>
public sealed class CallErrorEventArgs : EventArgs
{
    /// <summary>Human-readable description of the error.</summary>
    public string     Message   { get; }

    /// <summary>The call the error relates to, or <see langword="null"/> if not tied to a specific call.</summary>
    public ICall?     Call      { get; }

    /// <summary>The underlying exception, when one is available; otherwise <see langword="null"/>.</summary>
    public Exception? Exception { get; }

    internal CallErrorEventArgs(string message, ICall? call = null, Exception? ex = null)
        => (Message, Call, Exception) = (message, call, ex);
}
