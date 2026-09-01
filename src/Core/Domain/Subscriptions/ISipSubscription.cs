namespace CalloraVoipSdk.Core.Domain.Subscriptions;

/// <summary>
/// An active SIP subscription: somebody else's state, delivered as it changes (RFC 6665).
/// </summary>
/// <remarks>
/// <para>
/// This is how a telephone system knows that an extension is busy without polling it — subscribe to
/// the <c>dialog</c> package of a line and read <see cref="SipDialogInfo"/> out of each notification.
/// The same shape carries <c>presence</c> (RFC 3856) and anything else a registrar offers.
/// </para>
/// <para>
/// <b>Disposing unsubscribes.</b> A subscription the far end still believes in keeps generating
/// NOTIFYs at a process that is no longer listening, and a registrar that counts subscribers keeps
/// counting this one until its lease runs out.
/// </para>
/// </remarks>
public interface ISipSubscription : IAsyncDisposable
{
    /// <summary>The event package this subscription is for.</summary>
    string EventType { get; }

    /// <summary>Raised for every inbound NOTIFY, including the one that ends the subscription.</summary>
    event EventHandler<SipNotificationEventArgs>? Notified;

    /// <summary>Ends the subscription (SUBSCRIBE with <c>Expires: 0</c>). Calling it twice is harmless.</summary>
    Task UnsubscribeAsync(CancellationToken ct = default);
}
