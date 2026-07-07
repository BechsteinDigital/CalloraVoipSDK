using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Minimal <see cref="ICall"/> used only to give the real <c>CallMediaOrchestrator</c> a
/// stable <see cref="CallId"/> for one loopback call. The orchestrator only reads
/// <see cref="CallId"/> and probes for the concrete <c>Call</c> type (which this is not),
/// so every other member is intentionally unreachable in these tests.
///
/// This is not a transport or media mock: signaling, SDP, RTP/SRTP, and the media
/// orchestrator/factory are all the real production components.
/// </summary>
internal sealed class LoopbackTestCall : ICall
{
    public CallId CallId { get; } = CallId.New();
    public CallState State => CallState.Connected;
    public CallDirection Direction => CallDirection.Outbound;
    public string RemoteParty => "loopback";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public IPhoneLine Line => throw new NotSupportedException();
    public CallMediaParameters? MediaParameters => null;
    public CallQualitySnapshot QualitySnapshot { get; } =
        CallQualitySnapshot.CreateEmpty(DateTimeOffset.UtcNow);

#pragma warning disable CS0067 // Events are part of ICall but never raised by this stub.
    public event EventHandler<CallStateChangedEventArgs>? StateChanged;
    public event EventHandler<HoldStateChangedEventArgs>? HoldStateChanged;
    public event EventHandler<DtmfReceivedEventArgs>? DtmfReceived;
    public event EventHandler<TransferRequestedEventArgs>? TransferRequested;
    public event EventHandler<CallQualitySnapshotChangedEventArgs>? QualitySnapshotChanged;
#pragma warning restore CS0067

    public Task AcceptAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task HangupAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task HoldAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task UnholdAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default) => throw new NotSupportedException();
    public Task BlindTransferAsync(string targetUri, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<CallActionResult> RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<CallActionResult> RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<CallActionResult> SendInfoAsync(string contentType, string body, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<CallActionResult> SendOptionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<CallActionResult> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<CallActionResult> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
