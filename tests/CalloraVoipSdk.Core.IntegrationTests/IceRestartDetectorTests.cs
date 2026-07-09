using CalloraVoipSdk.Core.Application.Media.Ice;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies ICE restart detection (RFC 8445 §9.1.1.1): a restart is recognized when the peer's
/// ice-ufrag and/or ice-pwd change; the first negotiation and an ICE removal are not restarts.
/// </summary>
public sealed class IceRestartDetectorTests
{
    [Fact]
    public void First_negotiation_is_not_a_restart()
    {
        Assert.False(IceRestartDetector.IsRestart(null, null, "uNew", "pNew"));
        Assert.False(IceRestartDetector.IsRestart("", "", "uNew", "pNew"));
    }

    [Fact]
    public void Unchanged_credentials_are_not_a_restart()
    {
        Assert.False(IceRestartDetector.IsRestart("u1", "p1", "u1", "p1"));
    }

    [Fact]
    public void Changed_ufrag_is_a_restart()
    {
        Assert.True(IceRestartDetector.IsRestart("u1", "p1", "u2", "p1"));
    }

    [Fact]
    public void Changed_pwd_is_a_restart()
    {
        Assert.True(IceRestartDetector.IsRestart("u1", "p1", "u1", "p2"));
    }

    [Fact]
    public void Changed_both_is_a_restart()
    {
        Assert.True(IceRestartDetector.IsRestart("u1", "p1", "u2", "p2"));
    }

    [Fact]
    public void Removing_ice_credentials_is_not_a_restart()
    {
        Assert.False(IceRestartDetector.IsRestart("u1", "p1", null, null));
        Assert.False(IceRestartDetector.IsRestart("u1", "p1", "u2", null));
    }
}
