using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;
using Xunit.Abstractions;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// #310 probes: what a real browser actually does with H.264 and with an Encoded Transform. Both are
/// preconditions for the ticket's remaining criteria, and both are currently assumptions — the SDK has no
/// H.264 browser coverage at all, and no test has ever put a transform in the browser's send path.
/// </summary>
/// <remarks>
/// Probes, not gates: they assert what a third party does, and Playwright's Chromium is not Chrome — it is
/// commonly built without the proprietary codecs, which is exactly one of the things measured here. Run on
/// demand with <c>dotnet test --filter "Category=BrowserProbe"</c>.
/// </remarks>
[Trait("Category", "BrowserProbe")]
public sealed class H264BrowserProbeTests(ITestOutputHelper output)
{
    /// <summary>
    /// Can this browser negotiate H.264 with us at all? The answer decides how #310's H.264 criterion can be
    /// met: if Playwright's Chromium carries no H.264, the criterion's "or the reason is documented as a
    /// limit" branch applies to the test environment rather than to the SDK.
    /// </summary>
    [ChromiumFact]
    public async Task Chromium_reports_whether_it_negotiates_h264_with_the_sdk()
    {
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(InteropNetwork.LocalIPv4(), 0),
            AudioCodecs = ["opus"],
            EnableVideo = true,
            VideoCodecs = ["H264"],
        });
        await using var peer = client.CreatePeer();

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(await LoadVideoHtmlAsync());
        await bridge.StartAsync();

        var browserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerSdp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await foreach (var msg in bridge.Inbound.Reader.ReadAllAsync())
            {
                switch (msg.Type)
                {
                    case "ready": browserReady.TrySetResult(); break;
                    case "answer": answerSdp.TrySetResult(msg.Sdp!); break;
                }
            }
        });

        await using var browser = new BrowserPeer(BrowserEngine.Chromium);
        await browser.NavigateAsync(bridge.BaseUri);
        await browserReady.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = offer });
        var answer = await answerSdp.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var videoLine = answer.Split("\r\n").FirstOrDefault(l => l.StartsWith("m=video", StringComparison.Ordinal));
        var rejected = videoLine?.StartsWith("m=video 0 ", StringComparison.Ordinal) ?? true;
        var h264Rtpmaps = answer.Split("\r\n")
            .Where(l => l.Contains("H264", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        output.WriteLine($"offered:  {OfferedVideoCodecs(offer)}");
        output.WriteLine($"answered: {videoLine ?? "(no m=video)"}");
        output.WriteLine($"H264 lines in the answer: {h264Rtpmaps.Length}");
        foreach (var line in h264Rtpmaps.Take(6))
            output.WriteLine($"  {line}");

        Assert.False(
            rejected,
            $"Chromium rejected the H.264 video m-line: {videoLine ?? "(none)"}. If this browser build carries "
            + $"no H.264, #310's H.264 criterion cannot be met in this environment.");
        Assert.NotEmpty(h264Rtpmaps);
    }

    /// <summary>
    /// Negotiating H.264 in SDP is not the same as having an encoder for it. This connects for real and
    /// counts what arrives: frames, key frames, and where the key-frame flag came from (#310, ADR-071 — for
    /// H.264 the payload-derived answer reads only NAL headers, which stay clear even under encryption).
    /// </summary>
    [ChromiumFact]
    public async Task Chromium_reports_whether_it_actually_sends_h264_media()
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
        var bySource = new Dictionary<KeyFrameSource, int>();
        var firstFrameBytes = 0;
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Video) return;
            track.FrameReceived += (_, frame) =>
            {
                if (frames == 0) firstFrameBytes = frame.Payload.Length;
                frames++;
                if (frame.IsKeyFrame) keyFrames++;
                bySource[frame.KeyFrameSource] = bySource.GetValueOrDefault(frame.KeyFrameSource) + 1;
            };
        };

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
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTime.UtcNow < deadline && (frames < 30 || keyFrames == 0))
            await Task.Delay(500);

        output.WriteLine($"inbound H.264 frames: {frames}, key frames: {keyFrames}, first frame: {firstFrameBytes} bytes");
        foreach (var (source, count) in bySource)
            output.WriteLine($"  key-frame source {source}: {count}");

        Assert.True(
            frames > 0,
            $"Chromium negotiated H.264 but sent no decodable frames. Browser logs:\n  {browser.DumpLogs()}");
        Assert.True(keyFrames > 0, $"No key frame among {frames} inbound H.264 frames.");
    }

    /// <summary>
    /// The case #310 exists for: the browser encrypts its frames the way real systems do — everything past
    /// the first few bytes replaced, the rest left readable for packetiser and forwarder (Jitsi's
    /// <c>UNENCRYPTED_BYTES</c>) — and the SDK's clear-media H.264 path receives that stream. Measures how
    /// far reassembly gets, and cross-checks the SDK's key-frame answer against what the browser declared.
    /// </summary>
    [ChromiumTheory]
    // Jitsi's constants (lib-jitsi-meet/modules/e2ee/Context.ts). Tuned for VP8, whose payload header is
    // three bytes — for H.264 three bytes do not even cover an Annex-B start code, and the measurement below
    // shows the consequence: the browser's own packetiser emits almost nothing.
    [InlineData("jitsi (VP8-tuned)", 10, 3, false)]
    // What libwebrtc's frame cryptor does for H.264: the clear prefix covers the NAL headers, so a start code
    // (4) plus the header (1) survives, with room for the SPS/PPS a key frame carries.
    [InlineData("h264-aware", 16, 8, true)]
    public async Task Chromium_reports_what_the_sdk_receives_from_an_encrypting_h264_sender(
        string label, int clearKeyBytes, int clearDeltaBytes, bool expectMediaFlow)
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
        var bySource = new Dictionary<KeyFrameSource, int>();
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Video) return;
            track.FrameReceived += (_, frame) =>
            {
                frames++;
                if (frame.IsKeyFrame) keyFrames++;
                bySource[frame.KeyFrameSource] = bySource.GetValueOrDefault(frame.KeyFrameSource) + 1;
            };
        };

        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(
            await LoadTransformHtmlAsync(clearKeyBytes, clearDeltaBytes));
        await bridge.StartAsync();

        var browserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long browserFrames = 0, browserKeyFrames = 0;
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
                    case "transform":
                        browserFrames = msg.PacketsReceived ?? 0;
                        browserKeyFrames = msg.KeyFrames ?? 0;
                        break;
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
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTime.UtcNow < deadline && (frames < 30 || keyFrames == 0))
            await Task.Delay(500);

        // The decisive split: packets arriving but no frames coming out means the SDK's depacketiser discards
        // them (a limit of ours); almost no packets means the browser's own packetiser never emitted them
        // (a limit of the transform, on their side of the wire).
        var stats = peer.GetStats();
        output.WriteLine($"clear prefix: {label} (key {clearKeyBytes} / delta {clearDeltaBytes} bytes)");
        output.WriteLine($"browser encrypted {browserFrames} frames, {browserKeyFrames} of them key frames");
        output.WriteLine($"SDK inbound RTP: {stats.PacketsReceived} packets, {stats.BytesReceived} bytes");
        output.WriteLine($"SDK received {frames} frames, {keyFrames} reported as key frames");
        foreach (var (source, count) in bySource)
            output.WriteLine($"  key-frame source {source}: {count}");

        Assert.True(
            browserFrames > 0,
            $"The browser's transform never ran — no encrypted frames. Logs:\n  {browser.DumpLogs()}");

        if (expectMediaFlow)
        {
            Assert.True(
                frames >= 10,
                $"Only {frames} frames arrived from an encrypting sender that left the NAL headers readable "
                + $"({stats.PacketsReceived} inbound RTP packets). Logs:\n  {browser.DumpLogs()}");
            Assert.True(keyFrames > 0, $"No key frame among {frames} frames.");
            return;
        }

        // The measured failure, pinned rather than tolerated: with a clear prefix too short to keep the
        // Annex-B structure parseable, the browser's own packetiser stops emitting. The evidence that this is
        // its side of the wire and not ours is the RTP count — a handful of packets for hundreds of frames,
        // so nothing reached our depacketiser to be discarded.
        Assert.True(
            stats.PacketsReceived < browserFrames / 4,
            $"Expected the browser to drop nearly everything with a {clearDeltaBytes}-byte clear prefix, but "
            + $"{stats.PacketsReceived} RTP packets arrived for {browserFrames} encrypted frames — the "
            + $"measurement in #310 no longer holds and the ticket's conclusion needs revisiting.");
    }

    private static string OfferedVideoCodecs(string sdp) =>
        string.Join(", ", sdp.Split("\r\n").Where(l => l.StartsWith("a=rtpmap:", StringComparison.Ordinal)));

    private static async Task<string> LoadVideoHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer-video.html");
        return await File.ReadAllTextAsync(path);
    }

    private static async Task<string> LoadTransformHtmlAsync(int clearKeyBytes, int clearDeltaBytes)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer-video-transform.html");
        var html = await File.ReadAllTextAsync(path);
        return html.Replace(
            "__UNENCRYPTED_BYTES__",
            $"{{ key: {clearKeyBytes}, delta: {clearDeltaBytes}, undefined: 1 }}",
            StringComparison.Ordinal);
    }
}
