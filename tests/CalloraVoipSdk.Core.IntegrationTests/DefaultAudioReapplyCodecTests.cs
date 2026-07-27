using System.Net;
using CalloraVoipSdk.Core.Application.Convenience;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #17.3 regression: default audio must not pin the device to the PCMU/8k
/// <see cref="AudioConnectionParameters.Default"/> fallback forever. When the call reaches Connected before
/// its <see cref="ICall.MediaParameters"/> are negotiated, the attachment connects on Default; once the real
/// negotiated codec arrives it must re-apply the device to the negotiated codec, and it must NOT churn the
/// device when the effective parameters are unchanged.
/// </summary>
public sealed class DefaultAudioReapplyCodecTests : IDisposable
{
    private readonly MediaManager _media = new();
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public DefaultAudioReapplyCodecTests()
    {
        _channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        _call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            _channel, new FakePhoneLine(), NullLogger<Call>.Instance);
    }

    public void Dispose() => _channel.Dispose();

    private SdkConvenienceOrchestrator BuildOrchestrator(RecordingAudioDevice device) =>
        new(
            new PhoneLineManager(_ => throw new NotSupportedException("lines are not exercised here")),
            _media,
            device,
            NullLoggerFactory.Instance,
            videoDevice: null);

    [Fact]
    public async Task Connected_with_null_media_parameters_uses_the_default_codec()
    {
        var device = new RecordingAudioDevice();
        using var orchestrator = BuildOrchestrator(device);
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected); // MediaParameters still null → Default (PCMU/8k)

        await orchestrator.AttachDefaultAudioAsync(_call, CancellationToken.None);

        var only = Assert.Single(device.Connects);
        Assert.Equal("PCMU", only.CodecName);
        Assert.Equal(0, only.PayloadType);
        Assert.Equal(8000, only.SampleRate);
    }

    [Fact]
    public async Task Late_media_parameters_reapply_the_negotiated_codec()
    {
        var device = new RecordingAudioDevice();
        using var orchestrator = BuildOrchestrator(device);
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected); // Default connect first

        await orchestrator.AttachDefaultAudioAsync(_call, CancellationToken.None);
        Assert.Equal("PCMU", device.Connects[^1].CodecName);

        // The real negotiated codec (G722) arrives after the Default connect; a subsequent connect-eligible
        // state change re-evaluates the attachment, which must re-apply the device onto the negotiated codec.
        _call.SetMediaParameters(NegotiatedG722());
        _call.TransitionTo(CallState.OnHold);
        _call.TransitionTo(CallState.Connected);

        Assert.Equal("G722", device.Connects[^1].CodecName);
        Assert.Equal(9, device.Connects[^1].PayloadType);
        Assert.Equal(16000, device.Connects[^1].SampleRate); // G.722 hardware rate (RFC 3551)
        Assert.True(device.DisconnectCount >= 1, "the previous device wiring is dropped before the re-apply");
    }

    [Fact]
    public async Task Mid_call_renegotiation_reapplies_the_codec_without_a_state_change()
    {
        var device = new RecordingAudioDevice();
        using var orchestrator = BuildOrchestrator(device);
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected); // Default connect first (MediaParameters still null)

        await orchestrator.AttachDefaultAudioAsync(_call, CancellationToken.None);
        Assert.Equal("PCMU", device.Connects[^1].CodecName);

        // A mid-call re-INVITE codec change updates MediaParameters while the call STAYS Connected — no
        // StateChanged fires. The attachment must still re-apply the device onto the negotiated codec, driven by
        // Call.MediaParametersChanged rather than a state transition (#17.3 re-INVITE gap).
        _call.SetMediaParameters(NegotiatedG722());

        Assert.Equal("G722", device.Connects[^1].CodecName);
        Assert.Equal(16000, device.Connects[^1].SampleRate);
        Assert.True(device.DisconnectCount >= 1, "the previous device wiring is dropped before the re-apply");
    }

    [Fact]
    public async Task Unchanged_parameters_do_not_reconnect_the_device()
    {
        var device = new RecordingAudioDevice();
        using var orchestrator = BuildOrchestrator(device);

        // Negotiated up front: the Connected connect already uses the real codec, so a further connect-eligible
        // transition must NOT re-apply (effective parameters unchanged).
        _call.SetMediaParameters(NegotiatedG722());
        _call.TransitionTo(CallState.Ringing);
        _call.TransitionTo(CallState.Connected);

        await orchestrator.AttachDefaultAudioAsync(_call, CancellationToken.None);
        Assert.Single(device.Connects);

        _call.TransitionTo(CallState.OnHold);
        _call.TransitionTo(CallState.Connected);

        Assert.Single(device.Connects); // still one connect — no churn
    }

    private static CallMediaParameters NegotiatedG722() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 40000),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 40002),
        PayloadType = 9,
        CodecName = "G722",
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    private sealed class RecordingAudioDevice : IAudioDevice
    {
        public List<AudioConnectionParameters> Connects { get; } = new();
        public int DisconnectCount { get; private set; }
        public string Name => "recording";

        public void Connect(IMediaReceiver receiver, IMediaSender sender, AudioConnectionParameters parameters)
            => Connects.Add(parameters);

        public void Disconnect() => DisconnectCount++;
    }
}
