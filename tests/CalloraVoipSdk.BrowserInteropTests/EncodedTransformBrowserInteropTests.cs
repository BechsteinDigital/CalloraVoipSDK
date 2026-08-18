using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;
using Xunit.Abstractions;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// L4 — #310, letztes Kriterium: H.264-Empfang von einem Browser, dessen Frames durch einen Encoded
/// Transform gelaufen sind (WebRTC Encoded Transform / SFrame, RFC 9605). Der Payload ist bis auf die
/// Kopf-Bytes unlesbar; die Reassemblierung muss trotzdem tragen.
/// </summary>
/// <remarks>
/// <para>
/// Der Klartext-Anteil ist codec-gerecht gesetzt (Start-Code plus NAL-Header bleiben lesbar), so wie es
/// libwebrtcs Frame-Cryptor für H.264 tut. Warum das der richtige Parameter ist und was mit einem
/// VP8-getunten Präfix passiert, steht gemessen in <see cref="H264BrowserProbeTests"/> und in ADR-071 —
/// kurz: dann bricht der Sende-Packetiser des Browsers, bevor ein Paket den Rechner verlässt.
/// </para>
/// <para>
/// Was dieser Test zusichert, ist unser Empfangspfad, nicht das Verhalten des Browsers: dass ein
/// H.264-Strom, dessen Payload nur in den Kopf-Bytes lesbar ist, vollständig reassembliert wird und sein
/// Keyframe-Signal behält.
/// </para>
/// </remarks>
[Trait("Category", "BrowserInterop")]
public sealed class EncodedTransformBrowserInteropTests(ITestOutputHelper output)
{
    // Deckt Annex-B-Start-Code (4) + NAL-Header (1) mit Reserve; Keyframes tragen SPS/PPS und brauchen mehr.
    private const int ClearKeyBytes = 16;
    private const int ClearDeltaBytes = 8;

    [ChromiumFact]
    public async Task An_h264_stream_from_an_encrypting_browser_is_reassembled_with_its_key_frame_signal()
    {
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(InteropNetwork.LocalIPv4(), 0),
            AudioCodecs = ["opus"],
            EnableVideo = true,
            VideoCodecs = ["H264"],
        });
        await using var peer = client.CreatePeer();

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += (_, s) =>
        {
            if (s == PeerConnectionState.Connected) connected.TrySetResult();
        };

        var frames = 0;
        var keyFrames = 0;
        var emptyFrames = 0;
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Video) return;
            track.FrameReceived += (_, frame) =>
            {
                frames++;
                if (frame.IsKeyFrame) keyFrames++;
                if (frame.Payload.Length == 0) emptyFrames++;
            };
        };

        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(await LoadTransformHtmlAsync());
        await bridge.StartAsync();

        var browserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long browserFrames = 0;
        _ = Task.Run(async () =>
        {
            await foreach (var msg in bridge.Inbound.Reader.ReadAllAsync())
            {
                switch (msg.Type)
                {
                    case "ready": browserReady.TrySetResult(); break;
                    case "answer":
                        await peer.SetRemoteDescriptionAsync(msg.Sdp!);
                        answerApplied.TrySetResult();
                        break;
                    case "candidate": await peer.AddIceCandidateAsync(msg.Candidate!); break;
                    case "transform": browserFrames = msg.PacketsReceived ?? 0; break;
                }
            }
        });

        await using var browser = new BrowserPeer(BrowserEngine.Chromium);
        await browser.NavigateAsync(bridge.BaseUri);
        await browserReady.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = offer });
        _ = Task.Run(async () =>
        {
            await foreach (var c in pendingCandidates.Reader.ReadAllAsync())
                await bridge.SendAsync(new BridgeMessage { Type = "candidate", Candidate = c });
        });

        await answerApplied.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await peer.StartAsync();

        try { await connected.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            Assert.Fail($"Der SDK-Peer wurde nicht Connected. Browser-Logs:\n  {browser.DumpLogs()}");
        }

        // Video unter CI-CPU-Druck braucht Anlauf; 30 Frames sind gut eine Sekunde Video, sobald es fließt.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTime.UtcNow < deadline && (frames < 30 || keyFrames == 0))
            await Task.Delay(500);

        var stats = peer.GetStats();
        output.WriteLine($"browser transformed {browserFrames} frames; SDK: {frames} frames, {keyFrames} key, "
            + $"{stats.PacketsReceived} inbound RTP packets");

        Assert.True(
            frames >= 30,
            $"Nur {frames} Frames von einem verschlüsselnden H.264-Sender reassembliert "
            + $"({stats.PacketsReceived} eingehende RTP-Pakete, Browser transformierte {browserFrames}). "
            + $"Browser-Logs:\n  {browser.DumpLogs()}");
        Assert.True(keyFrames > 0, $"Kein Keyframe unter {frames} Frames — das Signal ging über die Strecke verloren.");
        Assert.Equal(0, emptyFrames);
    }

    private static async Task<string> LoadTransformHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer-video-transform.html");
        var html = await File.ReadAllTextAsync(path);
        return html.Replace(
            "__UNENCRYPTED_BYTES__",
            $"{{ key: {ClearKeyBytes}, delta: {ClearDeltaBytes}, undefined: 1 }}",
            StringComparison.Ordinal);
    }
}
