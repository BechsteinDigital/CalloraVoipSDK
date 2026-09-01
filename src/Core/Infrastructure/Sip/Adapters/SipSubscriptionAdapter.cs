using CalloraVoipSdk.Core.Domain.Subscriptions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Presents an internal subscription handle as the public <see cref="ISipSubscription"/>.
/// </summary>
/// <remarks>
/// A thin adapter rather than making the handle public: the handle carries the machinery a
/// subscription needs — the refresh loop, the dialog it lives in, the unsubscribe closure — and none
/// of that is anybody else's business. What an application needs is three things, and they are the
/// three on the interface.
/// </remarks>
internal sealed class SipSubscriptionAdapter : ISipSubscription
{
    private readonly SipSubscriptionHandle _handle;

    public SipSubscriptionAdapter(string eventType, SipSubscriptionHandle handle)
    {
        EventType = eventType;
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _handle.NotifyReceived += OnNotifyReceived;
    }

    /// <inheritdoc />
    public event EventHandler<SipNotificationEventArgs>? Notified;

    /// <inheritdoc />
    public string EventType { get; }

    /// <inheritdoc />
    public Task UnsubscribeAsync(CancellationToken ct = default) => _handle.UnsubscribeAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Unhooked before the handle goes: an event raised into a disposed adapter would reach
        // subscribers who have already been told the subscription is over.
        _handle.NotifyReceived -= OnNotifyReceived;
        await _handle.DisposeAsync().ConfigureAwait(false);
    }

    private void OnNotifyReceived(object? sender, SipNotifyReceivedEventArgs args) =>
        Notified?.Invoke(
            this,
            new SipNotificationEventArgs(
                args.EventType, args.SubscriptionState, args.IsTerminated, args.ContentType, args.Body));
}
