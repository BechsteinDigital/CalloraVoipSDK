using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class WebRtcVideoBrowserInteropTests
{
    private volatile BridgeMessage? _lastStats;
    private BridgeMessage? LastStats { get => _lastStats; set => _lastStats = value; }
    private volatile bool _videoEchoStarted;

    [BrowserRequiredFact]
    public async Task SdkOfferer_Exchanges_Vp8Video_And_Browser_Decodes()
    {
        // 1. SDK-Peer (Offerer) mit VP8-Video.
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AudioCodecs = ["opus"],
            EnableVideo = true,
            VideoCodecs = ["VP8"],
        });
        await using var peer = client.CreatePeer();

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += (_, s) =>
        {
            if (s == PeerConnectionState.Connected) connected.TrySetResult();
        };

        // Browser->SDK: Video-Frames zählen UND keyframe-bewusst zurück-echoen (SDK->Browser).
        // Das Echo startet erst ab dem ersten Keyframe, damit der Browser einen dekodierbaren Stream
        // (Keyframe zuerst) erhält — sonst kein framesDecoded.
        var inboundFrames = 0;
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Video) return;
            track.FrameReceived += (_, frame) =>
            {
                Interlocked.Increment(ref inboundFrames);
                if (!_videoEchoStarted && !frame.IsKeyFrame) return;   // warte auf ein Keyframe
                _videoEchoStarted = true;
                _ = peer.SendVideoFrameAsync(frame.Payload.ToArray(), frame.RtpTimestamp ?? 0);
            };
        };

        // 2. Signaling-Bridge + Candidate-Puffer.
        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(await LoadVideoHtmlAsync());
        await bridge.StartAsync();

        var browserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    case "stats": LastStats = msg; break;
                    case "log": /* Browser-Diagnose kommt über page.Console */ break;
                }
            }
        });

        // 3. Browser starten → lädt peer-video.html, öffnet WS, sendet "ready".
        await using var browser = new BrowserPeer();
        await browser.NavigateAsync(bridge.BaseUri);
        await browserReady.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // 4. Offer + Candidates.
        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = offer });
        _ = Task.Run(async () =>
        {
            await foreach (var c in pendingCandidates.Reader.ReadAllAsync())
                await bridge.SendAsync(new BridgeMessage { Type = "candidate", Candidate = c });
        });

        await answerApplied.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await peer.StartAsync();

        // 5. Assertions: connected + Browser->SDK-Video-Frames + Browser bytesReceived + framesDecoded.
        try { await connected.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            Assert.Fail($"SDK-Peer wurde nicht Connected. Browser-Logs:\n  {browser.DumpLogs()}");
        }

        // Video (Keyframe + Dekodieren) braucht mehr Zeit als Audio → 40 s Deadline.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTime.UtcNow < deadline &&
               (Volatile.Read(ref inboundFrames) < 10
                || (LastStats?.BytesReceived ?? 0) <= 0
                || (LastStats?.FramesDecoded ?? 0) <= 0))
            await Task.Delay(500);

        Assert.True(Volatile.Read(ref inboundFrames) >= 10,
            $"Browser->SDK: nur {Volatile.Read(ref inboundFrames)} VP8-Frames empfangen. Browser-Logs:\n  {browser.DumpLogs()}");
        Assert.True((LastStats?.BytesReceived ?? 0) > 0,
            $"SDK->Browser: bytesReceived={LastStats?.BytesReceived?.ToString() ?? "null"}. Browser-Logs:\n  {browser.DumpLogs()}");
        Assert.True((LastStats?.FramesDecoded ?? 0) > 0,
            $"SDK->Browser: der Browser DEKODIERTE keine Frames (framesDecoded={LastStats?.FramesDecoded?.ToString() ?? "null"}). Browser-Logs:\n  {browser.DumpLogs()}");
    }

    private static async Task<string> LoadVideoHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer-video.html");
        return await File.ReadAllTextAsync(path);
    }
}
