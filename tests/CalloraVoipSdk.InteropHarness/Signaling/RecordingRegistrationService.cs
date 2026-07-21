using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>
/// Fake-<c>ISipRegistrationService</c> für den Signaling-Soak: antwortet sofort mit 200/OK und
/// einem kurzen <c>EffectiveExpiresSeconds</c> (treibt den echten Refresh-Loop schnell) und
/// zeichnet CSeq + Call-ID jedes Zyklus auf.
/// </summary>
internal sealed class RecordingRegistrationService : ISipRegistrationService
{
    private readonly int _expiresSeconds;
    private readonly List<RegisterCycle> _cycles = new();
    private readonly object _gate = new();

    /// <summary>Initializes the service with the configured expires value.</summary>
    public RecordingRegistrationService(int expiresSeconds) => _expiresSeconds = expiresSeconds;

    /// <summary>Returns a snapshot of all recorded registration cycles so far.</summary>
    public IReadOnlyList<RegisterCycle> Cycles
    {
        get { lock (_gate) return _cycles.ToArray(); }
    }

    /// <inheritdoc />
    public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
    {
        lock (_gate) _cycles.Add(new RegisterCycle(request.StartCSeq, request.ExistingCallId));
        return Task.FromResult(new SipRegistrationResult
        {
            CallId = "soak-call-id",
            StatusCode = 200,
            EffectiveExpiresSeconds = _expiresSeconds,
            ContactUri = "sip:soak@127.0.0.1",
            Authenticated = true,
            NextCSeq = request.StartCSeq + 1,
        });
    }

    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        Task.FromResult(new SipRegistrationResult
        {
            CallId = "soak-call-id", StatusCode = 200, EffectiveExpiresSeconds = 0,
            ContactUri = "sip:soak@127.0.0.1", Authenticated = true, NextCSeq = request.StartCSeq + 1,
        });

    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        UnregisterAsync(request, ct);

    /// <inheritdoc />
    public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        RegisterAsync(request, ct);
}
