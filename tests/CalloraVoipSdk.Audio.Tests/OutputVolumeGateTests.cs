using CalloraVoipSdk.Audio.Abstractions.Processing;

namespace CalloraVoipSdk.Audio.Tests;

/// <summary>
/// The output-volume gate (issue #18, A4): while muted, a requested volume must not reach the
/// hardware, and unmuting must restore the last requested level — the platform devices store the
/// requested value regardless, but only program it when not muted.
/// </summary>
public sealed class OutputVolumeGateTests
{
    [Fact]
    public void While_unmuted_the_requested_volume_reaches_the_hardware()
    {
        Assert.Equal(0.7f, OutputVolumeGate.EffectiveHardwareVolume(0.7f, isMuted: false));
    }

    [Fact]
    public void While_muted_the_hardware_stays_silent_regardless_of_requested_volume()
    {
        Assert.Equal(0f, OutputVolumeGate.EffectiveHardwareVolume(0.9f, isMuted: true));
    }

    [Fact]
    public void Unmuting_restores_the_requested_level_that_was_set_during_mute()
    {
        // A volume change arrives during mute; it is stored but not applied.
        Assert.Equal(0f, OutputVolumeGate.EffectiveHardwareVolume(0.4f, isMuted: true));

        // On unmute the same stored value becomes the effective hardware level.
        Assert.Equal(0.4f, OutputVolumeGate.EffectiveHardwareVolume(0.4f, isMuted: false));
    }
}
