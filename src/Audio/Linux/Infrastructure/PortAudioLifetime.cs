using PortAudioSharp;

namespace CalloraVoipSdk.Audio.Linux;

/// <summary>
/// Process-wide, reference-counted access to the PortAudio backend for <see cref="LinuxAudioDevice"/>.
/// Every caller that needs PortAudio initialized acquires through this holder and releases when done,
/// so <c>Pa_Initialize</c> and <c>Pa_Terminate</c> stay balanced across the constructor, the static
/// device-enumeration helpers, and the instance streams (issue #18, A7). Enumeration helpers acquire
/// for the duration of the enumeration only (via <see cref="Acquire"/> in a <c>using</c>); a live
/// device holds one acquisition from construction until disposal.
/// </summary>
public static class PortAudioLifetime
{
    private static readonly PortAudioRefCountGuard Guard =
        new(PortAudio.Initialize, PortAudio.Terminate);

    /// <summary>
    /// Current number of outstanding PortAudio acquisitions across the process.
    /// </summary>
    public static int OutstandingAcquisitions => Guard.Count;

    /// <summary>
    /// Acquires PortAudio, initializing it if this is the first outstanding acquisition, and returns
    /// a scope that releases the acquisition when disposed.
    /// </summary>
    /// <returns>A disposable scope; dispose it (or let a <c>using</c> do so) to release.</returns>
    public static PortAudioLease Acquire()
    {
        Guard.Acquire();
        return new PortAudioLease(Guard);
    }
}
