using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Video;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Behavior tests for the public default-video convenience on the SDK facade (public video API, step b —
/// sub-slice 2b): <see cref="VoipClient"/> exposes <c>AttachDefaultVideoAsync</c>/<c>DetachDefaultVideoAsync</c>,
/// resolves the transport-only <see cref="IVideoDevice"/> from DI, and fails closed when none is registered.
/// The end-to-end connect/frame path is covered at the core layer; here we pin the facade seam.
/// </summary>
public sealed class DefaultVideoConvenienceFacadeTests
{
    private static SdkConfiguration TestConfiguration() => new()
    {
        UserAgent = "CalloraVoipSdk.Client.Tests/1.0",
        EnableAutomaticAudioDeviceSelection = false,
    };

    [Fact]
    public async Task Attach_without_a_registered_video_device_fails_closed()
    {
        using var client = new VoipClient(TestConfiguration());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AttachDefaultVideoAsync(new FakeCall()));
        Assert.Contains("video codec device", ex.Message);
    }

    [Fact]
    public async Task Attach_resolves_the_di_registered_video_device_and_does_not_fail_closed()
    {
        var device = new FakeVideoDevice();
        var services = new ServiceCollection();
        services.AddCallora(options =>
        {
            options.UserAgent = "CalloraVoipSdk.Client.Tests/1.0";
            options.EnableAutomaticAudioDeviceSelection = false;
        });
        services.AddSingleton<IVideoDevice>(device);

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IVoipClient>();

        // A ringing call defers the connect, so the device is not yet touched — but the attach only gets
        // this far (instead of throwing fail-closed) because the DI device was resolved and handed through.
        await client.AttachDefaultVideoAsync(new FakeCall());

        Assert.False(device.Connected);
    }

    [Fact]
    public async Task Detach_default_video_is_safe_when_nothing_is_attached()
    {
        using var client = new VoipClient(TestConfiguration());

        await client.DetachDefaultVideoAsync(new FakeCall());
    }

    [Fact]
    public async Task Attach_default_video_throws_after_dispose()
    {
        var client = new VoipClient(TestConfiguration());
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.AttachDefaultVideoAsync(new FakeCall()));
    }
}

/// <summary>Records whether the transport-only video device was connected.</summary>
internal sealed class FakeVideoDevice : IVideoDevice
{
    private int _connectCount;
    private int _disconnectCount;

    public string Name => "fake-facade-video-device";
    public bool Connected => _connectCount > _disconnectCount;

    public void Connect(IVideoReceiver receiver, IVideoSender sender, VideoConnectionParameters parameters) =>
        _connectCount++;

    public void Disconnect() => _disconnectCount++;
}

/// <summary>
/// Minimal <see cref="ICall"/> stub for the facade convenience seam: a ringing call whose only exercised
/// surface is its id, state, media parameters, and state-changed subscription. Every other member is out
/// of scope for these tests and throws if touched.
/// </summary>
internal sealed class FakeCall : ICall
{
    public CallId CallId { get; } = CallId.New();
    public CallState State => CallState.Ringing;
    public CallMediaParameters? MediaParameters => null;
    public CallIceState IceConnectionState => CallIceState.Disabled;

    public event EventHandler<CallStateChangedEventArgs>? StateChanged { add { } remove { } }
    public event EventHandler<HoldStateChangedEventArgs>? HoldStateChanged { add { } remove { } }
    public event EventHandler<DtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
    public event EventHandler<TransferRequestedEventArgs>? TransferRequested { add { } remove { } }
    public event EventHandler<CallQualitySnapshotChangedEventArgs>? QualitySnapshotChanged { add { } remove { } }
    public event EventHandler<CallIceConnectionStateChangedEventArgs>? IceConnectionStateChanged { add { } remove { } }

    public CallDirection Direction => throw new NotImplementedException();
    public string RemoteParty => throw new NotImplementedException();
    public DateTimeOffset StartedAt => throw new NotImplementedException();
    public IPhoneLine Line => throw new NotImplementedException();
    public CallQualitySnapshot QualitySnapshot => throw new NotImplementedException();
    public CallRtpStatistics? RtpStatistics => throw new NotImplementedException();
    public CallIceSnapshot? IceSnapshot => throw new NotImplementedException();
    public string? RemoteAssertedIdentity => throw new NotImplementedException();
    public string? Diversion => throw new NotImplementedException();

    public Task AcceptAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task HangupAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task HoldAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnholdAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task SendDtmfAsync(DtmfTone tone, CancellationToken ct = default) => throw new NotImplementedException();
    public Task BlindTransferAsync(string targetUri, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> AttendedTransferAsync(ICall consultationCall, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<CallActionResult> RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<CallActionResult> RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<CallActionResult> SendInfoAsync(string contentType, string body, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<CallActionResult> SendOptionsAsync(CancellationToken ct = default) => throw new NotImplementedException();

    public Task<CallActionResult> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<CallActionResult> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
