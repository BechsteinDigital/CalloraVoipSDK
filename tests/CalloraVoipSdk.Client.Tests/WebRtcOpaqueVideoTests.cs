using System.Net;
using CalloraVoipSdk.DependencyInjection;
using CalloraVoipSdk.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// L4 — #223 / ADR-068: the public opaque-video-frames switch. The app encrypts each frame end to end before
/// handing it over (WebRTC Encoded Transform / SFrame, RFC 9605), so the SDK must carry it without reading it.
/// These tests cover the public surface of that decision — the immutable configuration, the DI options path, the
/// fluent builder — and then prove it end to end: two real clients exchange a frame of pure ciphertext, which the
/// default (clear-media) path cannot transport at all.
/// </summary>
public sealed class WebRtcOpaqueVideoTests
{
    // ── the public surface ───────────────────────────────────────────────────────────────────

    /// <summary>Off unless asked for: an existing app's media path must not change under it.</summary>
    [Fact]
    public void The_switch_is_off_by_default_on_both_option_surfaces()
    {
        Assert.False(new WebRtcConfiguration().OpaqueVideoFrames);
        Assert.False(new WebRtcOptions().OpaqueVideoFrames);
    }

    /// <summary>The DI options path projects onto the immutable configuration the client actually reads.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_options_path_carries_the_switch_onto_the_configuration(bool opaque)
    {
        var options = new WebRtcOptions { EnableVideo = true, OpaqueVideoFrames = opaque };

        var config = options.ToConfiguration(loggerFactory: null);

        Assert.Equal(opaque, config.OpaqueVideoFrames);
    }

    /// <summary>
    /// The fluent builder's opaque variant is <see cref="CalloraWebRtcBuilder.WithVideo"/> plus the switch: video
    /// on, codec preference honoured, frames opaque. A host that only calls <c>WithVideo</c> stays clear-media.
    /// </summary>
    [Fact]
    public void The_builder_enables_video_and_the_switch_together()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc().WithOpaqueVideo("VP8");

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<WebRtcOptions>>().Value;

        Assert.True(options.EnableVideo);
        Assert.True(options.OpaqueVideoFrames);
        Assert.Equal(["VP8"], options.VideoCodecs);
    }

    [Fact]
    public void The_plain_video_builder_leaves_frames_clear()
    {
        var services = new ServiceCollection();
        services.AddCalloraWebRtc().WithVideo("VP8");

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<WebRtcOptions>>().Value;

        Assert.True(options.EnableVideo);
        Assert.False(options.OpaqueVideoFrames);
    }

    // ── end to end through the public facade ─────────────────────────────────────────────────

    /// <summary>
    /// The whole point, over a real transport: two clients configured for opaque video connect, and a frame of pure
    /// ciphertext arrives byte-identically at the peer with no key-frame claim attached. This is the only test that
    /// covers the full public chain — <see cref="WebRtcConfiguration"/> → <c>CreatePeer</c> → the peer's transport
    /// policy → the payload format on the wire — so a break anywhere along it fails here.
    /// </summary>
    [Fact]
    public async Task Two_opaque_clients_exchange_a_ciphertext_video_frame_byte_identically()
    {
        await using var offerer = OpaqueVideoClient();
        await using var answerer = OpaqueVideoClient();
        await using var a = offerer.CreatePeer();
        await using var b = answerer.CreatePeer();

        var aConnected = Connected(a);
        var bConnected = Connected(b);
        var arrived = new TaskCompletionSource<EncodedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.TrackReceived += (_, track) =>
        {
            if (track.Kind == TrackKind.Video)
                track.FrameReceived += (_, frame) => arrived.TrySetResult(
                    new EncodedFrame(frame.Payload.ToArray(), frame.RtpTimestamp, frame.IsKeyFrame, frame.PresentationTimeUsec, frame.Rid));
        };

        var offer = a.CreateOffer();
        var answer = await b.SetRemoteDescriptionAsync(offer);
        await a.SetRemoteDescriptionAsync(answer);
        await a.StartAsync();
        await b.StartAsync();
        await Task.WhenAll(aConnected, bConnected).WaitAsync(TimeSpan.FromSeconds(20));

        // Pure ciphertext: no Annex-B start codes, no VP8 syntax — nothing the SDK could interpret even if it tried.
        var ciphertext = new byte[4_000];
        new Random(223).NextBytes(ciphertext);

        var timestamp = 90_000u;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!arrived.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await a.SendVideoFrameAsync(ciphertext, timestamp);
            timestamp += 3_000;
            await Task.Delay(20, overall.Token);
        }

        var received = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ciphertext, received.Payload.ToArray());
        Assert.False(received.IsKeyFrame); // "unknown", not "no" — the signal is not in the payload (#223 follow-up)
    }

    /// <summary>
    /// The default path cannot do it, which is why the switch exists: the clear-media H.264 packetiser looks for
    /// Annex-B NAL boundaries that encrypted content does not have and refuses the frame outright.
    /// </summary>
    [Fact]
    public async Task A_clear_media_client_refuses_a_ciphertext_frame()
    {
        await using var rtc = new WebRtcClient(new WebRtcConfiguration
        {
            EnableVideo = true,
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });
        await using var a = rtc.CreatePeer();
        await using var b = rtc.CreatePeer();

        var offer = a.CreateOffer();
        await a.SetRemoteDescriptionAsync(await b.SetRemoteDescriptionAsync(offer));
        await a.StartAsync();

        var ciphertext = new byte[1_000];
        new Random(223).NextBytes(ciphertext);

        await Assert.ThrowsAsync<ArgumentException>(() => a.SendVideoFrameAsync(ciphertext, 90_000u));
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private static WebRtcClient OpaqueVideoClient() => new(new WebRtcConfiguration
    {
        EnableVideo = true,
        OpaqueVideoFrames = true,
        // Ephemeral loopback: early-bind yields a live m-line and a fixed port would collide on CI.
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    });

    private static Task Connected(IPeerConnection peer)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += (_, state) =>
        {
            if (state == PeerConnectionState.Connected)
                tcs.TrySetResult();
        };
        return tcs.Task;
    }
}
