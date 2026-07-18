using CalloraVoipSdk.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Startup-validation gate for <see cref="VoipOptions"/> (HARD-E9). A negative
/// <see cref="VoipOptions.InboundMediaTimeout"/> is neither the documented disable sentinel
/// (<see cref="TimeSpan.Zero"/>) nor a valid interval and must be rejected before it can feed a
/// call-teardown timer.
/// </summary>
public sealed class VoipOptionsValidatorTests
{
    private static readonly VoipOptionsValidator Validator = new();

    [Fact]
    public void Default_options_pass_validation()
    {
        var result = Validator.Validate(name: null, new VoipOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Negative_inbound_media_timeout_is_rejected()
    {
        var options = new VoipOptions { InboundMediaTimeout = TimeSpan.FromSeconds(-5) };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("InboundMediaTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_inbound_media_timeout_is_accepted_as_the_disable_sentinel()
    {
        var options = new VoipOptions { InboundMediaTimeout = TimeSpan.Zero };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Succeeded);
    }
}
