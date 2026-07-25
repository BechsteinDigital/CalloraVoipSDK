using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// CF-066b Slice 4: the <see cref="VoipClient"/> PUBLISH-lifecycle facade methods publish from the first
/// registered line, exactly like <see cref="VoipClient.PublishAsync"/>. With no line registered they must fail
/// with the same "register a line first" guard rather than a null-reference. (Forwarding to a real line is
/// covered at the line/adapter level in PhoneLinePublishTests / SipLineChannelPublishIfMatchTests, since a
/// registered line requires live SIP transport that cannot be faked into VoipClient.Lines here.)
/// </summary>
public sealed class VoipClientPublishLifecycleTests
{
    private static VoipConfiguration TestConfiguration() => new()
    {
        UserAgent = "CalloraVoipSdk.Client.Tests/1.0",
        EnableAutomaticAudioDeviceSelection = false,
    };

    [Fact]
    public async Task RefreshPublicationAsync_without_a_registered_line_throws()
    {
        using var client = new VoipClient(TestConfiguration());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RefreshPublicationAsync("presence", "etag-1"));
    }

    [Fact]
    public async Task ModifyPublicationAsync_without_a_registered_line_throws()
    {
        using var client = new VoipClient(TestConfiguration());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ModifyPublicationAsync("presence", "etag-1", "<pidf/>", "application/pidf+xml"));
    }

    [Fact]
    public async Task RemovePublicationAsync_without_a_registered_line_throws()
    {
        using var client = new VoipClient(TestConfiguration());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RemovePublicationAsync("presence", "etag-1"));
    }
}
