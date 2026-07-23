using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Event data for an inbound out-of-dialog SIP MESSAGE (RFC 3428), raised by the ingress signaling
/// service. The line adapter translates it into the public domain instant-message value object.
/// </summary>
internal sealed class SipIncomingMessageEventArgs : EventArgs
{
    /// <summary>Creates inbound MESSAGE event data from the request's parsed fields.</summary>
    public SipIncomingMessageEventArgs(
        string from,
        string to,
        string callId,
        string contentType,
        string body,
        IPEndPoint remoteEndPoint)
    {
        From = from;
        To = to;
        CallId = callId;
        ContentType = contentType;
        Body = body;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>The MESSAGE From header (sender).</summary>
    public string From { get; }

    /// <summary>The MESSAGE To header (recipient — used to match the request to a line).</summary>
    public string To { get; }

    /// <summary>The MESSAGE Call-ID (correlation only; no dialog).</summary>
    public string CallId { get; }

    /// <summary>The MESSAGE Content-Type (RFC 3428 default <c>text/plain</c>).</summary>
    public string ContentType { get; }

    /// <summary>The MESSAGE body.</summary>
    public string Body { get; }

    /// <summary>The peer's signaling transport address — used for line matching.</summary>
    public IPEndPoint RemoteEndPoint { get; }
}
