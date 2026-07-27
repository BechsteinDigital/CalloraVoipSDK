namespace CalloraVoipSdk.Audio.Abstractions.Processing;

/// <summary>
/// Pure decision logic for the interaction of output mute and output volume on hardware whose
/// single volume control is the only mute mechanism (for example NAudio's <c>WaveOutEvent</c>,
/// which has no independent mute). While muted, a requested volume must be remembered but not
/// applied to the hardware, so that unmuting restores the caller's intended level rather than a
/// stale one and a mid-mute volume change does not audibly leak through the mute (issue #18, A4).
/// The type is immutable and platform-neutral so the policy can be unit-tested without hardware.
/// </summary>
public static class OutputVolumeGate
{
    /// <summary>
    /// Computes the volume that should be written to hardware given the requested volume and the
    /// current mute state. When muted the hardware stays silent (0); the requested value is only
    /// stored by the caller as the restore level. When not muted the requested value is applied.
    /// </summary>
    /// <param name="requestedVolume">The caller's desired output gain (already validated).</param>
    /// <param name="isMuted">Whether output is currently muted.</param>
    /// <returns>The gain to program into the hardware volume control.</returns>
    public static float EffectiveHardwareVolume(float requestedVolume, bool isMuted)
        => isMuted ? 0f : requestedVolume;
}
