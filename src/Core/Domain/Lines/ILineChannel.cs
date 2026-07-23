using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Messages;

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
    /// Stops SIP registration and awaits the explicit de-registration (REGISTER with Expires:0,
    /// RFC 3261 §10.2.2). Unlike <see cref="StopRegistration"/> — which fires the de-register as
    /// best-effort cleanup for the synchronous dispose path — the returned task completes only after
    /// the de-register round-trip, so callers of <see cref="IPhoneLine.UnregisterAsync"/> can await
    /// the binding removal.
    /// </summary>
    Task StopRegistrationAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Wires the callback invoked for an inbound out-of-dialog SIP MESSAGE (RFC 3428) addressed to this
    /// line. The message has already been answered 200 OK by the transport layer.
    /// </summary>
    void SetMessageHandler(Action<SipInstantMessage> onMessage);
}
