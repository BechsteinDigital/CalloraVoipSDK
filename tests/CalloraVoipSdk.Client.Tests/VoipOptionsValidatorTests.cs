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

    [Fact]
    public void Negative_media_silence_notify_delay_is_rejected()
    {
        var options = new VoipOptions { MediaSilenceNotifyAfter = TimeSpan.FromSeconds(-1) };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("MediaSilenceNotifyAfter", StringComparison.Ordinal));
    }

    /// <summary>
    /// #261: the silence notification is the early warning for the teardown. Configured at or after the
    /// teardown it could only ever fire once the call was already gone — accepted silently, it would look
    /// configured but never arrive.
    /// </summary>
    [Theory]
    [InlineData(30, 30)]
    [InlineData(45, 30)]
    public void A_notify_delay_that_cannot_precede_the_teardown_is_rejected(int notifySeconds, int timeoutSeconds)
    {
        var options = new VoipOptions
        {
            MediaSilenceNotifyAfter = TimeSpan.FromSeconds(notifySeconds),
            InboundMediaTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("MediaSilenceNotifyAfter", StringComparison.Ordinal));
    }

    /// <summary>Either half disabled removes the ordering constraint — there is no pair left to order.</summary>
    [Theory]
    [InlineData(0, 30)]
    [InlineData(45, 0)]
    public void A_disabled_half_lifts_the_ordering_constraint(int notifySeconds, int timeoutSeconds)
    {
        var options = new VoipOptions
        {
            MediaSilenceNotifyAfter = TimeSpan.FromSeconds(notifySeconds),
            InboundMediaTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Succeeded);
    }
}
