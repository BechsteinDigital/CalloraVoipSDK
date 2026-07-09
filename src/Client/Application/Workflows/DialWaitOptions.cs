using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk;

/// <summary>
/// Options for convenience outbound dialing flows.
/// </summary>
public sealed class DialWaitOptions
{
    /// <summary>
    /// Default dial-wait options.
    /// </summary>
    public static DialWaitOptions Default { get; } = new();

    /// <summary>
    /// Optional low-level dial options passed to <see cref="Domain.Lines.IPhoneLine.DialAsync"/>.
    /// </summary>
    public DialOptions? DialOptions { get; init; }

    /// <summary>
    /// Maximum time to wait for the call to reach a connected state.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Automatically sends hangup when wait completion is caused by timeout.
    /// </summary>
    public bool HangupOnTimeout { get; init; } = true;

    /// <summary>
    /// Automatically sends hangup when wait completion is caused by cancellation.
    /// </summary>
    public bool HangupOnCancellation { get; init; } = true;
}
