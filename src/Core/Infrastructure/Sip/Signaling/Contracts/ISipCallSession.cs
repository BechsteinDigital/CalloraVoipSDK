using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Represents one SIP call dialog signaling session.
/// Implementations own transport-side resources and must be disposed when no longer needed.
/// </summary>
internal interface ISipCallSession : IDisposable
{
    /// <summary>
    /// SIP Call-ID for the dialog.
    /// </summary>
    string CallId { get; }

    /// <summary>
    /// Local SIP URI for this dialog.
    /// </summary>
    string LocalUri { get; }

    /// <summary>
    /// Remote SIP URI for this dialog.
    /// </summary>
    string RemoteUri { get; }

    /// <summary>
    /// Current dialog signaling state.
    /// </summary>
    SipDialogState State { get; }

    /// <summary>
    /// Last known dialog termination reason (RFC3326), when available.
    /// </summary>
    SipDialogTerminationReason? LastTerminationReason { get; }

    /// <summary>
    /// True when the dialog originated from an inbound INVITE.
    /// </summary>
    bool IsInbound { get; }

    /// <summary>
    /// Last known remote asserted identity URI from trusted peers (RFC 3325).
    /// </summary>
    string? RemoteAssertedIdentity { get; }

    /// <summary>
    /// Remote SDP body received from the far end.
    /// For inbound sessions: the SDP from the initial INVITE body.
    /// For outbound sessions: the SDP from the 200 OK answer.
    /// <see langword="null"/> until the session reaches <see cref="SipDialogState.Established"/>.
    /// </summary>
    string? RemoteSdp { get; }

    /// <summary>
    /// Local dialog tag (the tag this UA contributed: From-tag on an outbound leg, To-tag on an
    /// inbound leg). Used to build an RFC 3891 <c>Replaces</c> for attended transfer. Defaults to
    /// <see langword="null"/> so existing implementations keep compiling.
    /// </summary>
    string? LocalTag => null;

    /// <summary>
    /// Remote dialog tag (the far end's tag). <see langword="null"/> until the dialog is
    /// established. Used to build an RFC 3891 <c>Replaces</c> for attended transfer. Defaults to
    /// <see langword="null"/> so existing implementations keep compiling.
    /// </summary>
    string? RemoteTag => null;

    /// <summary>
    /// The most recent local SDP the session put on the wire (the answer we sent on an inbound
    /// leg / re-INVITE, or an offer). Used by the media adapter to recover our own SDES key on a
    /// re-INVITE rekey. Defaults to <see langword="null"/> so existing implementations keep
    /// compiling and the adapter falls back to the SDP it built itself.
    /// </summary>
    string? LocalSdp => null;

    /// <summary>
    /// Local signaling transport endpoint. Used to derive the local IP address for media.
    /// </summary>
    IPEndPoint LocalSignalingEndPoint { get; }

    /// <summary>
    /// Remote signaling transport endpoint when known (inbound: source of the INVITE,
    /// outbound: resolved request target). Used to pick the local interface to advertise
    /// for media without any DNS lookup. Defaults to <see langword="null"/> so existing
    /// implementations keep compiling.
    /// </summary>
    IPEndPoint? RemoteSignalingEndPoint => null;

    /// <summary>
    /// Sets the public host/port to advertise in in-dialog Contact URIs (behind NAT), so a
    /// trunk peer can route the ACK to a 2xx and later in-dialog requests. The default
    /// implementation is a no-op for sessions that do not support it.
    /// </summary>
    void SetAdvertisedPublicContact(string? host, int? port) { }

    /// <summary>
    /// Raised whenever the dialog state changes.
    /// </summary>
    event EventHandler<SipDialogStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when remote party hold state changes.
    /// True = remote placed dialog on hold.
    /// </summary>
    event EventHandler<bool>? RemoteHoldChanged;

    /// <summary>
    /// Raised when a remote INFO message delivers a DTMF tone.
    /// </summary>
    event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived;

    /// <summary>
    /// Raised when the remote side requests call transfer via REFER.
    /// </summary>
    event EventHandler<SipTransferRequestedEventArgs>? TransferRequested;

    /// <summary>
    /// Raised when the remote side sends an in-dialog SUBSCRIBE request.
    /// </summary>
    event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested;

    /// <summary>
    /// Raised when an inbound SIP NOTIFY is received for this dialog (RFC 6665 §6.1.1).
    /// </summary>
    event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived;

    /// <summary>
    /// Answers an inbound ringing INVITE with 200 OK.
    /// </summary>
    Task AnswerAsync(
        string? sessionDescription = null,
        CancellationToken ct = default);

    /// <summary>
    /// Rejects an inbound ringing INVITE with a 4xx, 5xx, or 6xx status code.
    /// Use instead of <see cref="HangupAsync"/> when a specific rejection reason must be signaled
    /// (e.g. 486 Busy Here, 603 Decline, 480 Temporarily Unavailable).
    ///
    /// Only valid when <see cref="IsInbound"/> is <c>true</c> and the dialog is in
    /// <see cref="SipDialogState.Ringing"/> state.
    /// </summary>
    /// <param name="statusCode">SIP rejection status code (4xx–6xx). Defaults to 486 Busy Here.</param>
    /// <param name="reasonPhrase">Optional reason phrase override. Defaults to the standard phrase for the code.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RejectAsync(
        int statusCode = 486,
        string? reasonPhrase = null,
        CancellationToken ct = default);

    /// <summary>
    /// Terminates the dialog (BYE/CANCEL/reject depending on current state).
    /// </summary>
    Task HangupAsync(
        CancellationToken ct = default,
        SipDialogTerminationReason? reason = null);

    /// <summary>
    /// Redirects an inbound ringing INVITE with a 3xx response and one or more Contact URIs.
    /// Per RFC 3261 §8.3, the UAC will retry at the supplied Contact target(s).
    ///
    /// Valid status codes are 300–399. Use 302 (Moved Temporarily) for temporary deflection
    /// such as forwarding to voicemail or an alternative extension.
    ///
    /// Only valid when <see cref="IsInbound"/> is <c>true</c> and the dialog is in
    /// <see cref="SipDialogState.Ringing"/> state.
    /// </summary>
    /// <param name="contactUris">
    /// One or more target SIP URIs to include in the Contact header of the 3xx response.
    /// Must not be empty.
    /// </param>
    /// <param name="statusCode">3xx SIP status code (300–399). Defaults to 302.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RedirectAsync(
        IReadOnlyList<string> contactUris,
        int statusCode = 302,
        CancellationToken ct = default);

    /// <summary>
    /// Sends re-INVITE hold (a=sendonly) and transitions to OnHold.
    /// </summary>
    Task HoldAsync(
        string? sessionDescription = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends re-INVITE unhold (a=sendrecv) and transitions to Established.
    /// </summary>
    Task UnholdAsync(
        string? sessionDescription = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends DTMF tone via SIP INFO (RFC 2833 <c>application/dtmf-relay</c>).
    /// Valid digits: 0–9, *, #, A–D.
    /// Only valid on established or on-hold dialogs.
    /// </summary>
    /// <param name="digit">DTMF digit character to send.</param>
    /// <param name="durationMs">Tone duration in milliseconds. Defaults to 160 ms.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendDtmfAsync(
        char digit,
        int durationMs = 160,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP INFO with explicit content type/body.
    /// </summary>
    Task SendInfoAsync(
        string contentType,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP REFER for call transfer.
    /// Returns true when the remote side accepts the request (2xx).
    /// </summary>
    /// <param name="referTo">Target SIP URI for the transfer.</param>
    /// <param name="referredBy">Optional RFC 3892 Referred-By identity URI.</param>
    /// <param name="suppressSubscription">
    /// When <c>true</c>, sends <c>Refer-Sub: false</c> and <c>Require: norefersub</c> (RFC 4488)
    /// so the remote UA does not create an implicit NOTIFY subscription for the REFER.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> SendReferAsync(
        string referTo,
        string? referredBy = null,
        bool suppressSubscription = false,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP OPTIONS to probe remote capabilities/liveness.
    /// Returns true on any successful 2xx response.
    /// </summary>
    Task<bool> SendOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP SUBSCRIBE for one event package.
    /// Returns true when subscription was accepted (2xx).
    /// </summary>
    Task<bool> SendSubscribeAsync(
        string eventType,
        int expiresSeconds = 300,
        string? acceptHeader = null,
        string? body = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends an in-dialog SIP NOTIFY for an active subscription (RFC 6665 §4.2.2).
    /// Returns true when the subscriber responded with 2xx.
    /// </summary>
    Task<bool> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType = null,
        string? body = null,
        CancellationToken ct = default);
}
