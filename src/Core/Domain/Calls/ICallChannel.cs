namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Internal port: abstracts the SIP+RTP transport for a single call.
/// Implemented by infrastructure; never exposed publicly.
/// </summary>
internal interface ICallChannel : IDisposable
{
    // ── Actions ───────────────────────────────────────────────────────────────
    Task AnswerAsync(CancellationToken ct);
    Task HangupAsync();
    Task HoldAsync();
    Task UnholdAsync();
    Task SendDtmfAsync(byte dtmfCode);
    Task RejectAsync(int statusCode, string? reasonPhrase, CancellationToken ct);
    Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode, CancellationToken ct);
    Task SendInfoAsync(string contentType, string body, CancellationToken ct);
    Task<bool> SendOptionsAsync(CancellationToken ct);
    Task<bool> SendSubscribeAsync(
        string eventType,
        int expiresSeconds,
        string? acceptHeader,
        string? body,
        CancellationToken ct);
    Task<bool> SendNotifyAsync(
        string eventType,
        string subscriptionState,
        string? contentType,
        string? body,
        CancellationToken ct);

    Task<bool> BlindTransferAsync(string targetUri, TimeSpan timeout, CancellationToken ct);
    Task<bool> AttendedTransferAsync(ICallChannel target, TimeSpan timeout, CancellationToken ct);
    Task SendAudioFrameAsync(CallAudioFrame frame, CancellationToken ct = default);
    Task SendVideoFrameAsync(CallVideoFrame frame, CancellationToken ct = default);

    // ── Callbacks (set once by Call aggregate before any events can fire) ─────
    void BindCallbacks(CallChannelCallbacks callbacks);
    void AddAudioFrameListener(Action<CallAudioFrame> onFrame);
    void RemoveAudioFrameListener(Action<CallAudioFrame> onFrame);
    void AddVideoFrameListener(Action<CallVideoFrame> onFrame);
    void RemoveVideoFrameListener(Action<CallVideoFrame> onFrame);

    // ── Remote identity (inbound) ─────────────────────────────────────────────

    /// <summary>
    /// Peer-asserted identity URI (P-Asserted-Identity, RFC 3325) from the inbound INVITE when the
    /// peer is trusted; <see langword="null"/> otherwise. Default implementation returns null.
    /// </summary>
    string? RemoteAssertedIdentity => null;

    /// <summary>
    /// First Diversion header URI (RFC 5806) from the inbound INVITE, or <see langword="null"/>.
    /// Default implementation returns null.
    /// </summary>
    string? Diversion => null;

    /// <summary>
    /// SDP body from a provisional (180/183) early-media response, or <see langword="null"/>.
    /// Default implementation returns null.
    /// </summary>
    string? EarlyMediaSdp => null;

    // ── Media negotiation ─────────────────────────────────────────────────────

    /// <summary>
    /// Raised by the infrastructure adapter once SDP offer/answer is complete and RTP
    /// parameters are known. The Application layer subscribes to this event to start
    /// the actual media session (RTP send/receive).
    /// </summary>
    event EventHandler<CallMediaParameters>? MediaParametersNegotiated;

    /// <summary>
    /// Delivers one inbound audio frame received from the network.
    /// Called by the application media orchestrator when an RTP packet arrives.
    /// All registered audio frame listeners are notified synchronously.
    /// </summary>
    void DeliverInboundAudioFrame(CallAudioFrame frame);

    /// <summary>
    /// Sets (or clears) the delegate used by <see cref="SendAudioFrameAsync"/> to route
    /// audio to the network. Called by the application media orchestrator once the media
    /// session is started. Pass <see langword="null"/> to disable sending.
    /// </summary>
    void SetAudioSendDelegate(Func<CallAudioFrame, CancellationToken, Task>? sendDelegate);

    /// <summary>
    /// Delivers one inbound video frame received from the network.
    /// Called by the application media orchestrator when a complete encoded video frame
    /// has been reassembled from RTP. All registered video frame listeners are notified synchronously.
    /// </summary>
    void DeliverInboundVideoFrame(CallVideoFrame frame);

    /// <summary>
    /// Sets (or clears) the delegate used by <see cref="SendVideoFrameAsync"/> to route
    /// video to the network. Called by the application media orchestrator once the video
    /// media session is started. Pass <see langword="null"/> to disable sending.
    /// </summary>
    void SetVideoSendDelegate(Func<CallVideoFrame, CancellationToken, Task>? sendDelegate);

    /// <summary>
    /// Sets (or clears) the delegate used by <see cref="SendDtmfAsync(byte)"/> to route
    /// DTMF via RTP telephone-event (RFC 4733).
    /// Called by the application media orchestrator when the RTP session is started.
    /// Implementations may keep a fallback path (for example SIP INFO) when no delegate is set.
    /// </summary>
    void SetDtmfSendDelegate(Func<byte, int, CancellationToken, Task>? sendDelegate)
    {
    }

    /// <summary>
    /// Delivers one inbound DTMF tone from the media path (for example RTP telephone-event)
    /// to the call channel callbacks.
    /// Called by the application media orchestrator.
    /// </summary>
    void DeliverInboundDtmf(byte toneCode, int durationMs)
    {
    }
}
