using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Per-call outgoing-audio mute: a local send-path gate on <see cref="ICall"/> that stops sending this
/// call's audio to the peer without SIP signalling and independent of the device-wide mute.
/// </summary>
public sealed class PerCallMuteTests
{
    [Fact]
    public async Task Mute_drops_this_calls_outgoing_audio_and_unmute_resumes()
    {
        using var channel = CreateChannel();
        ICallChannel c = channel;

        var sent = 0;
        c.SetAudioSendDelegate((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.CompletedTask;
        });
        var frame = new CallAudioFrame(new byte[] { 1, 2, 3 }, 0, 160);

        await c.SendAudioFrameAsync(frame);
        Assert.Equal(1, sent);
        Assert.False(c.IsOutgoingAudioMuted);

        c.SetOutgoingAudioMuted(true);
        Assert.True(c.IsOutgoingAudioMuted);
        await c.SendAudioFrameAsync(frame); // muted → not sent
        Assert.Equal(1, sent);

        c.SetOutgoingAudioMuted(false);
        await c.SendAudioFrameAsync(frame); // resumed
        Assert.Equal(2, sent);
    }

    [Fact]
    public async Task MuteAsync_on_the_call_gates_only_this_calls_send_path()
    {
        using var channel = CreateChannel();
        var call = new Call(
            CallId.New(),
            CallDirection.Outbound,
            "sip:peer@test.invalid",
            channel,
            new FakePhoneLine(),
            NullLogger<Call>.Instance);

        var sent = 0;
        ((ICallChannel)channel).SetAudioSendDelegate((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.CompletedTask;
        });
        var frame = new CallAudioFrame(new byte[] { 9 }, 0, 160);

        Assert.False(call.IsMuted);
        await ((ICallChannel)channel).SendAudioFrameAsync(frame);
        Assert.Equal(1, sent);

        await call.MuteAsync(true);
        Assert.True(call.IsMuted);
        await ((ICallChannel)channel).SendAudioFrameAsync(frame); // muted → dropped
        Assert.Equal(1, sent);

        await call.MuteAsync(false);
        Assert.False(call.IsMuted);
        await ((ICallChannel)channel).SendAudioFrameAsync(frame); // resumed
        Assert.Equal(2, sent);
    }

    private static SipCoreCallChannel CreateChannel() => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        SrtpPolicy.Disabled,
        "test");
}
