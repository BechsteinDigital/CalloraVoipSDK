using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// L4 — #225: the Dependency Descriptor's layer information at the SDK surface. The parse and the per-lane
/// state are covered in Core; what this proves is the part an SFU actually consumes — that the layer a sender
/// declares survives the whole receive path (track → session → peer → <see cref="RemoteTrack"/>) and arrives
/// on <see cref="EncodedFrame"/>, instead of being read and dropped one layer below the API.
/// </summary>
public sealed class WebRtcVideoLayerInformationTests
{
    /// <summary>
    /// Two real clients, a real transport, and a frame carrying the descriptor the SDK writes: the receiver
    /// reports the sender's layer. The SDK declares L1T1 — it does not encode video, so it knows of no ladder
    /// to describe — which makes spatial 0 / temporal 0 the honest answer here; what a scalable sender's
    /// deeper structure resolves to is pinned in Core's <c>DependencyDescriptorReceiveTests</c>.
    /// </summary>
    [Fact]
    public async Task A_received_frame_reports_the_layer_the_sender_declared()
    {
        await using var offerer = VideoClient();
        await using var answerer = VideoClient();
        await using var a = offerer.CreatePeer();
        await using var b = answerer.CreatePeer();

        var aConnected = Connected(a);
        var bConnected = Connected(b);
        var arrived = new TaskCompletionSource<(int? Spatial, int? Temporal, bool IsKeyFrame, KeyFrameSource Source)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        b.TrackReceived += (_, track) =>
        {
            if (track.Kind == TrackKind.Video)
                track.FrameReceived += (_, frame) =>
                    arrived.TrySetResult((frame.SpatialId, frame.TemporalId, frame.IsKeyFrame, frame.KeyFrameSource));
        };

        var offer = a.CreateOffer();
        await a.SetRemoteDescriptionAsync(await b.SetRemoteDescriptionAsync(offer));
        await a.StartAsync();
        await b.StartAsync();
        await Task.WhenAll(aConnected, bConnected).WaitAsync(TimeSpan.FromSeconds(20));

        // A single-packet VP8 key frame (RFC 7741 §9.1): P bit clear in the uncompressed data chunk.
        var keyFrame = new byte[] { 0x00, 0x00, 0x00, 0x9D, 0x01, 0x2A, 0x40, 0x01, 0xB0, 0x00 };

        var timestamp = 90_000u;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!arrived.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await a.SendVideoFrameAsync(keyFrame, timestamp, isKeyFrame: true, overall.Token);
            timestamp += 3_000;
            await Task.Delay(20, overall.Token);
        }

        var received = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, received.Spatial);
        Assert.Equal(0, received.Temporal);
        Assert.True(received.IsKeyFrame);
        // #310: and the frame says where that flag came from — the header, not a payload guess.
        Assert.Equal(KeyFrameSource.RtpHeaderExtension, received.Source);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private static WebRtcClient VideoClient() => new(new WebRtcConfiguration
    {
        EnableVideo = true,
        VideoCodecs = ["VP8"],
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
