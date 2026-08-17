using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;
using Xunit.Abstractions;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// #225 probe: does a real browser accept the Dependency Descriptor on a VP8 m-line? The descriptor is
/// codec-agnostic by design, and secondary sources claim browsers use it across codecs — but the SDK only
/// speaks VP8 and H.264, and the last time an assumption about what a peer puts on the wire went untested
/// (#261, RTCP during media silence) it was wrong. So this measures instead: offer the extension, print
/// what comes back in the answer, and let the result decide how much of #225 is reachable.
/// </summary>
/// <remarks>
/// Deliberately a probe, not a gate — and therefore <b>outside</b> the <c>BrowserInterop</c> category the CI
/// job runs. It asserts what a third party does, so leaving it in the gate would let a Chrome release that
/// drops the extension fail unrelated pull requests. Run it on demand:
/// <c>dotnet test --filter "Category=BrowserProbe"</c>.
/// </remarks>
[Trait("Category", "BrowserProbe")]
public sealed class DependencyDescriptorProbeTests(ITestOutputHelper output)
{
    private const string DependencyDescriptorUri =
        "https://aomediacodec.github.io/av1-rtp-spec/#dependency-descriptor-rtp-header-extension";

    [ChromiumFact]
    public async Task Chromium_answer_reports_whether_the_dependency_descriptor_is_accepted()
    {
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(InteropNetwork.LocalIPv4(), 0),
            AudioCodecs = ["opus"],
            EnableVideo = true,
            VideoCodecs = ["VP8"],
        });
        await using var peer = client.CreatePeer();

        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

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

        var offeredExtmaps = ExtmapLines(offer);
        var answeredExtmaps = ExtmapLines(answer);
        var accepted = answeredExtmaps.Any(l => l.Contains(DependencyDescriptorUri, StringComparison.Ordinal));

        // The probe's output IS the result. Failing with it is how the measurement becomes visible; the
        // assertion below is what #225 needs to be true, so a red here is the finding, not a broken test.
        Assert.True(
            accepted,
            $"Chromium did not accept the Dependency Descriptor on a VP8 m-line.\n"
            + $"  offered: {string.Join(" | ", offeredExtmaps)}\n"
            + $"  answered: {string.Join(" | ", answeredExtmaps)}");
    }

    /// <summary>
    /// The follow-up question the first probe leaves open, and the one the remaining acceptance criterion of
    /// #225 hangs on: accepting the extension in the answer is not the same as <em>writing</em> it. Browsers
    /// emit the descriptor mainly for AV1 and VP9, and the SDK speaks VP8 and H.264. So this receives a real
    /// Chromium video stream and reports how many of its frames arrived with layer information.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the measurement can and cannot see: <see cref="EncodedFrame.SpatialId"/> is non-null only when a
    /// descriptor arrived <b>and</b> its template resolved against a structure the sender declared. A run of
    /// nothing but nulls therefore means "no usable descriptor on this stream" — it cannot distinguish "sent
    /// none" from "sent one whose structure never arrived". For the open criterion that distinction does not
    /// change the answer: either way there is no header-derived key-frame flag to compare against the payload.
    /// </para>
    /// <para>A probe, not a gate — same reasoning as above; it asserts what a third party does.</para>
    /// </remarks>
    [ChromiumFact]
    public async Task Chromium_reports_whether_its_own_video_carries_the_dependency_descriptor()
    {
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(InteropNetwork.LocalIPv4(), 0),
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

        // Frames arrive serially on the peer's single receive loop, so plain counters are enough.
        var frames = 0;
        var withLayer = 0;
        var keyFrames = 0;
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Video) return;
            track.FrameReceived += (_, frame) =>
            {
                frames++;
                if (frame.SpatialId is not null || frame.TemporalId is not null) withLayer++;
                if (frame.IsKeyFrame) keyFrames++;
            };
        };

        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(await LoadVideoHtmlAsync());
        await bridge.StartAsync();

        var browserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerSdp = string.Empty;
        _ = Task.Run(async () =>
        {
            await foreach (var msg in bridge.Inbound.Reader.ReadAllAsync())
            {
                switch (msg.Type)
                {
                    case "ready": browserReady.TrySetResult(); break;
                    case "answer":
                        answerSdp = msg.Sdp!;
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
        while (DateTime.UtcNow < deadline && (frames < 30 || withLayer == 0))
            await Task.Delay(500);

        var accepted = ExtmapLines(answerSdp)
            .Any(l => l.Contains(DependencyDescriptorUri, StringComparison.Ordinal));

        // Printed whichever way the assertions go: the numbers are the measurement, and a green run that
        // reports them is worth more than one that only says "true".
        output.WriteLine($"inbound frames: {frames}");
        output.WriteLine($"  with layer information: {withLayer}");
        output.WriteLine($"  key frames: {keyFrames}");
        output.WriteLine($"  extension accepted in the answer: {accepted}");

        // The counts ARE the result. A red here is the finding: Chromium answers with the extension but does
        // not put it on its own VP8 stream, which is what decides how the last criterion of #225 can be met.
        Assert.True(frames > 0, $"No inbound video at all — the probe measured nothing. Browser logs:\n  {browser.DumpLogs()}");
        Assert.True(
            withLayer > 0,
            $"Chromium sent no usable Dependency Descriptor on its VP8 stream.\n"
            + $"  frames: {frames}, of them with layer information: {withLayer}, key frames: {keyFrames}\n"
            + $"  extension accepted in the answer: {accepted}");
    }

    private static IReadOnlyList<string> ExtmapLines(string sdp) =>
        [.. sdp.Split("\r\n").Where(l => l.StartsWith("a=extmap:", StringComparison.Ordinal))];

    private static async Task<string> LoadVideoHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer-video.html");
        return await File.ReadAllTextAsync(path);
    }
}
