namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// One admitted inbound-connection slot from <see cref="SipConnectionAdmissionControl"/>. Held for the
/// connection's lifetime; idempotent dispose runs the release callback exactly once, freeing the global and
/// per-remote counts (#158 P1-3).
/// </summary>
internal sealed class SipConnectionAdmissionLease : IDisposable
{
    private readonly Action _release;
    private int _released;

    /// <summary>
    /// Creates a lease that runs <paramref name="release"/> once when first disposed.
    /// </summary>
    public SipConnectionAdmissionLease(Action release)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        _release();
    }
}
