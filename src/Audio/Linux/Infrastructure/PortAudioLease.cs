namespace CalloraVoipSdk.Audio.Linux;

/// <summary>
/// A single outstanding PortAudio acquisition obtained from <see cref="PortAudioLifetime.Acquire"/>.
/// Disposing releases the acquisition exactly once; a live device keeps one lease for its lifetime,
/// while the enumeration helpers dispose theirs as soon as the enumeration completes (issue #18, A7).
/// </summary>
public sealed class PortAudioLease : IDisposable
{
    private PortAudioRefCountGuard? _guard;

    internal PortAudioLease(PortAudioRefCountGuard guard)
    {
        _guard = guard;
    }

    /// <summary>
    /// Releases this acquisition. Idempotent: a second dispose does not release twice.
    /// </summary>
    public void Dispose()
    {
        // Interlocked ensures a concurrent double-dispose releases the underlying count only once.
        var guard = Interlocked.Exchange(ref _guard, null);
        guard?.Release();
    }
}
