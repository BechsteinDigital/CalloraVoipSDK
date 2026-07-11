using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Dispatches a call session's application-facing notification events (DTMF, transfer, subscription,
/// NOTIFY) with fault isolation: a throwing consumer handler is logged, not propagated, so a faulty
/// subscriber never breaks the SIP signaling path. Injected per session; the caller passes the
/// event's delegate and the sender, so the events stay declared on the session itself.
/// </summary>
internal sealed class SipCallSessionEventDispatcher
{
    private readonly ILogger _logger;

    /// <summary>
    /// Creates an event dispatcher that logs consumer-handler failures to <paramref name="logger"/>.
    /// </summary>
    public SipCallSessionEventDispatcher(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Raises <c>DtmfReceived</c>, isolating a throwing handler.
    /// </summary>
    public void RaiseDtmf(
        EventHandler<SipDtmfReceivedEventArgs>? handler,
        object sender,
        byte toneCode,
        int durationMilliseconds,
        string callId)
    {
        try
        {
            handler?.Invoke(sender, new SipDtmfReceivedEventArgs(toneCode, durationMilliseconds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: DTMF callback failed.", callId);
        }
    }

    /// <summary>
    /// Raises <c>TransferRequested</c> and returns caller acceptance (false when unhandled or on failure).
    /// </summary>
    public bool RaiseTransferRequested(
        EventHandler<SipTransferRequestedEventArgs>? handler,
        object sender,
        string referTo,
        string referredBy,
        string callId)
    {
        if (handler is null)
            return false;

        var args = new SipTransferRequestedEventArgs(referTo, referredBy);
        try
        {
            handler.Invoke(sender, args);
            return args.Accept;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: transfer callback failed.", callId);
            return false;
        }
    }

    /// <summary>
    /// Raises <c>SubscriptionRequested</c> and returns caller acceptance. Defaults to acceptance when
    /// no handler is registered so the SUBSCRIBE lifecycle stays deterministic in headless/SDK-only
    /// integrations.
    /// </summary>
    public bool RaiseSubscriptionRequested(
        EventHandler<SipSubscriptionRequestedEventArgs>? handler,
        object sender,
        string eventType,
        int expiresSeconds,
        string? acceptHeader,
        string callId)
    {
        if (handler is null)
            return true;

        var args = new SipSubscriptionRequestedEventArgs(eventType, expiresSeconds, acceptHeader);
        try
        {
            handler.Invoke(sender, args);
            return args.Accept;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: subscription callback failed.", callId);
            return false;
        }
    }

    /// <summary>
    /// Raises <c>NotifyReceived</c>, isolating a throwing handler.
    /// </summary>
    public void RaiseNotifyReceived(
        EventHandler<SipNotifyReceivedEventArgs>? handler,
        object sender,
        string eventType,
        string subscriptionState,
        bool isTerminated,
        string? contentType,
        string? body,
        string callId)
    {
        try
        {
            handler?.Invoke(
                sender,
                new SipNotifyReceivedEventArgs(eventType, subscriptionState, isTerminated, contentType, body));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session {CallId}: NOTIFY callback failed.", callId);
        }
    }
}
