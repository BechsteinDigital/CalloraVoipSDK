namespace CalloraVoipSdk.Core.Domain.Subscriptions;

/// <summary>One inbound NOTIFY for an active subscription (RFC 6665 §6.1.1).</summary>
public sealed class SipNotificationEventArgs(
    string eventType,
    string subscriptionState,
    bool isTerminated,
    string? contentType,
    string? body) : EventArgs
{
    /// <summary>Event package from the <c>Event</c> header — <c>presence</c>, <c>dialog</c>, <c>refer</c>.</summary>
    public string EventType { get; } = eventType;

    /// <summary>The <c>Subscription-State</c> as received: <c>active</c>, <c>pending</c>, <c>terminated</c>.</summary>
    public string SubscriptionState { get; } = subscriptionState;

    /// <summary>
    /// Whether this NOTIFY ends the subscription.
    /// </summary>
    /// <remarks>
    /// The last one still carries a body, and it is usually the interesting one: a presence watcher
    /// that stops reading here misses the state the other side left behind.
    /// </remarks>
    public bool IsTerminated { get; } = isTerminated;

    /// <summary>MIME type of <see cref="Body"/>, e.g. <c>application/pidf+xml</c>.</summary>
    public string? ContentType { get; } = contentType;

    /// <summary>
    /// The event-state document, unparsed.
    /// </summary>
    /// <remarks>
    /// Handed over as received on purpose: <see cref="SipPresence"/> and <see cref="SipDialogInfo"/>
    /// read the two documents this SDK understands, and a package it does not know still reaches the
    /// application intact rather than being dropped for not fitting a model.
    /// </remarks>
    public string? Body { get; } = body;
}
