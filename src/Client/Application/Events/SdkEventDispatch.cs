using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk;

/// <summary>
/// The one event-raise contract for the SDK's public facades (#166 P3-14).
/// </summary>
/// <remarks>
/// <para>
/// An SDK event is raised from inside an SDK path — SIP signalling, a peer's state machine, the telemetry
/// publish, the media receive loop — and the application's handler is foreign code on that path. The contract
/// is therefore the one P1-3 established for telemetry, applied everywhere: <b>a subscriber's fault is logged
/// and swallowed, never propagated into the SDK path, and never allowed to keep the remaining subscribers from
/// running.</b> Facades snapshot their delegate first (inside their event lock, where they have one) and raise
/// it through this type outside the lock, so a handler may subscribe or unsubscribe without deadlocking (K3).
/// </para>
/// <para>
/// Before this, the Client layer had three different disciplines side by side: the SIP facade forwarded its
/// events with a bare <c>Invoke</c> (a throwing handler reached the signalling path and blocked the later
/// subscribers), the peer facade snapshotted under a lock but still invoked the whole multicast delegate in one
/// call (isolating nothing), and only the telemetry path isolated per subscriber.
/// </para>
/// </remarks>
internal static class SdkEventDispatch
{
    /// <summary>
    /// Raises <paramref name="handlers"/> with each subscriber isolated from the others and from the caller.
    /// A null delegate is a no-op.
    /// </summary>
    /// <param name="handlers">The delegate snapshot to invoke.</param>
    /// <param name="sender">The facade raising the event (never the inner component — the public sender).</param>
    /// <param name="args">The event payload.</param>
    /// <param name="logger">Logs a subscriber fault instead of propagating it.</param>
    /// <param name="eventName">The event's name, for the log message.</param>
    internal static void Raise<TArgs>(
        EventHandler<TArgs>? handlers, object sender, TArgs args, ILogger logger, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)subscriber)(sender, args);
            }
            catch (Exception ex)
            {
                LogSubscriberFault(logger, ex, eventName);
            }
        }
    }

    /// <summary>
    /// Raises a payload-free <see cref="EventHandler"/> with each subscriber isolated. A null delegate is a no-op.
    /// </summary>
    internal static void Raise(EventHandler? handlers, object sender, ILogger logger, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)subscriber)(sender, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogSubscriberFault(logger, ex, eventName);
            }
        }
    }

    /// <summary>
    /// Raises an <see cref="Action{T}"/>-shaped internal fan-out (the telemetry sink's shape) with each
    /// subscriber isolated. A null delegate is a no-op.
    /// </summary>
    internal static void Raise<TRecord>(Action<TRecord>? handlers, TRecord record, ILogger logger, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action<TRecord>)subscriber)(record);
            }
            catch (Exception ex)
            {
                LogSubscriberFault(logger, ex, eventName);
            }
        }
    }

    /// <summary>
    /// The per-frame variant for events raised on the media hot path: one guarded invocation of the whole
    /// multicast delegate, so a throwing subscriber still cannot reach the receive loop, but no invocation list
    /// is walked and nothing is allocated per frame (K3 — no avoidable allocation on the media hot path). The
    /// trade-off is deliberate and confined to per-frame events: a throwing subscriber does keep the later ones
    /// from seeing that one frame. Multi-subscriber isolation on a per-frame stream belongs to the tap fan-out
    /// (<see cref="WebRtc.IMediaTap"/>), which keeps a copy-on-write array for exactly that reason.
    /// </summary>
    internal static void RaiseOnMediaPath<TArgs>(
        EventHandler<TArgs>? handlers, object sender, TArgs args, ILogger logger, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers(sender, args);
        }
        catch (Exception ex)
        {
            LogSubscriberFault(logger, ex, eventName);
        }
    }

    private static void LogSubscriberFault(ILogger logger, Exception ex, string eventName)
        => logger.LogWarning(
            ex, "A {Event} subscriber threw; the fault was isolated so the SDK path is unaffected.", eventName);
}
