using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// #225 probe: does a real browser accept the Dependency Descriptor on a VP8 m-line? The descriptor is
/// codec-agnostic by design, and secondary sources claim browsers use it across codecs — but the SDK only
/// speaks VP8 and H.264, and the last time an assumption about what a peer puts on the wire went untested
/// (#261, RTCP during media silence) it was wrong. So this measures instead: offer the extension, print
/// what comes back in the answer, and let the result decide how much of #225 is reachable.
/// </summary>
/// <remarks>
/// Deliberately a probe, not a gate: it reports the browser's answer rather than asserting a shape. It runs
/// under the existing BrowserInterop category so it is opt-in like the rest of the browser matrix.
/// </remarks>
[Trait("Category", "BrowserInterop")]
public sealed class DependencyDescriptorProbeTests
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

    private static IReadOnlyList<string> ExtmapLines(string sdp) =>
        [.. sdp.Split("\r\n").Where(l => l.StartsWith("a=extmap:", StringComparison.Ordinal))];

    private static async Task<string> LoadVideoHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer-video.html");
        return await File.ReadAllTextAsync(path);
    }
}
