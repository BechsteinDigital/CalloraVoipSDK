namespace CalloraVoipSdk.Core.Domain.Messages;

/// <summary>
/// An inbound SIP pager-mode instant message (RFC 3428). MESSAGE is stateless — it opens no dialog; the
/// SDK answers it 200 OK and surfaces its content here for the application. Immutable value object.
/// </summary>
public sealed class SipInstantMessage
{
    /// <summary>The sender's address (the MESSAGE From header, name-addr or URI form).</summary>
    public string From { get; }

    /// <summary>The recipient the message was addressed to (the MESSAGE To header).</summary>
    public string To { get; }

    /// <summary>The message body — for example the SMS/IM text.</summary>
    public string Body { get; }

    /// <summary>The MIME content type of <see cref="Body"/> (RFC 3428 default <c>text/plain</c>).</summary>
    public string ContentType { get; }

    /// <summary>The Call-ID of the MESSAGE request — a correlation token only; no dialog is created.</summary>
    public string CallId { get; }

    /// <summary>Creates an inbound instant-message value object.</summary>
    public SipInstantMessage(string from, string to, string body, string contentType, string callId)
    {
        From = from ?? throw new ArgumentNullException(nameof(from));
        To = to ?? throw new ArgumentNullException(nameof(to));
        Body = body ?? string.Empty;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "text/plain" : contentType;
        CallId = callId ?? string.Empty;
    }
}
