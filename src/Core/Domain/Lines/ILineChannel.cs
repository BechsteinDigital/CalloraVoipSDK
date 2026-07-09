using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>
/// Internal port: abstracts SIP registration and outbound dialing for a phone line.
/// </summary>
internal interface ILineChannel : IDisposable
{
    /// <summary>
    /// Starts SIP registration and wires callbacks for state transitions and reconnect events.
    /// </summary>
    /// <param name="onStateChange">Invoked on every <see cref="LineState"/> transition.</param>
    /// <param name="onReconnecting">
    /// Invoked when a reconnect attempt begins.  Parameter is the one-based attempt number.
    /// </param>
    /// <param name="onReconnectFailed">
    /// Invoked when re-registration fails permanently.
    /// First parameter is the <see cref="ReregisterFailReason"/>; second is the total attempt count.
    /// </param>
    void StartRegistration(
        Action<LineState> onStateChange,
        Action<int>? onReconnecting = null,
        Action<ReregisterFailReason, int>? onReconnectFailed = null);

    void StopRegistration();

    /// <summary>
    /// Prepare the transport channel for an outbound call, without sending INVITE yet.
    /// This allows the domain Call aggregate to bind callbacks first.
    /// </summary>
    ICallChannel PrepareOutboundChannel(DialOptions options);

    /// <summary>
    /// Start dialing on a prepared outbound channel.
    /// Implementations should send INVITE and return after dial bootstrap.
    /// </summary>
    Task StartOutboundDialAsync(
        ICallChannel channel,
        string targetUri,
        DialOptions options,
        CancellationToken ct);

    void SetInboundHandler(Action<ICallChannel, string> onInbound);
}
