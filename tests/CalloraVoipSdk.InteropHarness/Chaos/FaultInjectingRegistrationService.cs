using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.InteropHarness.Chaos;

/// <summary>
/// A registrar that can be toggled between healthy (200 OK) and faulting (as if the registrar were
/// unreachable — <c>RegisterAsync</c> fails with a transient error). Used by the CORE-011 chaos gate to
/// prove the <c>SipLineChannel</c> registration loop degrades gracefully under a SIP provider outage:
/// it keeps retrying (RFC 3261 re-REGISTER with back-off) without wedging, and recovers when the registrar
/// comes back. A transient <see cref="TimeoutException"/> (not a 401/403) drives the loop's transient-failure
/// path, not the permanent-auth-failure path.
/// </summary>
internal sealed class FaultInjectingRegistrationService : ISipRegistrationService
{
    private readonly int _expiresSeconds;
    private volatile bool _faulting;
    private long _attempts;

    public FaultInjectingRegistrationService(int expiresSeconds, bool initiallyFaulting)
    {
        _expiresSeconds = expiresSeconds;
        _faulting = initiallyFaulting;
    }

    /// <summary>REGISTER attempts seen so far — evidence the loop keeps retrying rather than wedging.</summary>
    public long Attempts => Interlocked.Read(ref _attempts);

    /// <summary>Toggles the registrar between reachable (200 OK) and unreachable (transient failure).</summary>
    public void SetFault(bool faulting) => _faulting = faulting;

    /// <inheritdoc />
    public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _attempts);
        if (_faulting)
            return Task.FromException<SipRegistrationResult>(
                new TimeoutException("Registrar unreachable (chaos fault)."));
        return Task.FromResult(Ok(request, _expiresSeconds));
    }

    // Unregister paths always succeed so a StopRegistration during a fault can tear down cleanly.
    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        Task.FromResult(Ok(request, 0));

    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        UnregisterAsync(request, ct);

    /// <inheritdoc />
    public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
        RegisterAsync(request, ct);

    private static SipRegistrationResult Ok(SipRegistrationRequest request, int expiresSeconds) => new()
    {
        CallId = "chaos-call-id",
        StatusCode = 200,
        EffectiveExpiresSeconds = expiresSeconds,
        ContactUri = "sip:chaos@127.0.0.1",
        Authenticated = true,
        NextCSeq = request.StartCSeq + 1,
    };
}
