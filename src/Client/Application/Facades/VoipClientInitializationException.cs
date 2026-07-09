namespace CalloraVoipSdk;

/// <summary>
/// Signals that the VoIP SDK client could not be initialized due to runtime environment constraints.
/// </summary>
public sealed class VoipClientInitializationException : InvalidOperationException
{
    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    /// <param name="message">Describes why initialization failed.</param>
    /// <param name="innerException">The underlying exception that caused the failure.</param>
    public VoipClientInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
