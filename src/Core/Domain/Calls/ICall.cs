using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.Domain.Calls;

public interface ICall
{
    CallId        CallId      { get; }
    CallState     State       { get; }
    CallDirection Direction   { get; }
    string        RemoteParty { get; }
    DateTimeOffset StartedAt  { get; }

    /// <summary>The phone line this call belongs to.</summary>
    IPhoneLine Line { get; }

    /// <summary>
    /// Negotiated media parameters (codec, endpoints). Set once
    /// <see cref="CallState.Connected"/> is reached; null before that.
    /// </summary>
    CallMediaParameters? MediaParameters { get; }

    /// <summary>
    /// Latest quality snapshot derived from RTP/RTCP runtime metrics.
    /// Before media starts, this value is an empty baseline snapshot.
    /// </summary>
    CallQualitySnapshot QualitySnapshot { get; }

    // ── Events ────────────────────────────────────────────────────────────────
    event EventHandler<CallStateChangedEventArgs>?  StateChanged;
    event EventHandler<HoldStateChangedEventArgs>?  HoldStateChanged;
    event EventHandler<DtmfReceivedEventArgs>?      DtmfReceived;
    event EventHandler<TransferRequestedEventArgs>? TransferRequested;
    event EventHandler<CallQualitySnapshotChangedEventArgs>? QualitySnapshotChanged;

    // ── Actions ───────────────────────────────────────────────────────────────
    Task AcceptAsync(CancellationToken ct = default);
    Task HangupAsync(CancellationToken ct = default);
    Task HoldAsync(CancellationToken ct = default);
    Task UnholdAsync(CancellationToken ct = default);
    Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default);
    Task BlindTransferAsync(string targetUri, CancellationToken ct = default);

    /// <summary>
    /// Attended transfer: transfer this call to the party in <paramref name="consultationCall"/>.
    /// Both calls must be Connected or OnHold.
    /// </summary>
    Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default);

    /// <summary>
    /// Rejects an inbound ringing call with a 4xx/5xx/6xx SIP status.
    /// </summary>
    Task<CallActionResult> RejectAsync(
        int statusCode = 486,
        string? reasonPhrase = null,
        CancellationToken ct = default);

    /// <summary>
    /// Redirects an inbound ringing call with a 3xx SIP response and contact targets.
    /// </summary>
    Task<CallActionResult> RedirectAsync(
        IReadOnlyList<string> contactUris,
        int statusCode = 302,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP INFO.
    /// </summary>
    Task<CallActionResult> SendInfoAsync(
        string contentType,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP OPTIONS.
    /// </summary>
    Task<CallActionResult> SendOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP SUBSCRIBE.
    /// </summary>
    Task<CallActionResult> SendSubscribeAsync(
        string eventType,
        int expiresSeconds = 300,
        string? acceptHeader = null,
        string? body = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends in-dialog SIP NOTIFY.
    /// </summary>
    Task<CallActionResult> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType = null,
        string? body = null,
        CancellationToken ct = default);
}
