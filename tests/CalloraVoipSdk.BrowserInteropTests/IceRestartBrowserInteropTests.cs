using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// ICE restart against a real browser (#226). Everything else about the restart is proven against our own
/// loopback, which can only ever confirm that we agree with ourselves. What a browser adds is the one thing
/// loopback cannot: whether the SDP we emit is recognised as a restart by an implementation we did not write.
/// <para>
/// The load-bearing assertion is therefore the <em>browser's</em> behaviour — its answer must carry a rotated
/// ice-ufrag, which per RFC 8445 §9.1.1.1 it only does when it has understood the offer as an ICE restart. That
/// media then keeps flowing is the second half: the restart re-ran connectivity checks without disturbing the
/// DTLS session underneath.
/// </para>
/// </summary>
[Trait("Category", "BrowserInterop")]
public sealed class IceRestartBrowserInteropTests
{
    private volatile BridgeMessage? _lastStats;
    private BridgeMessage? LastStats { get => _lastStats; set => _lastStats = value; }

    [ChromiumFact] public Task IceRestart_Chromium() => RunIceRestartInterop(BrowserEngine.Chromium);
    [FirefoxFact]  public Task IceRestart_Firefox()  => RunIceRestartInterop(BrowserEngine.Firefox);

    private async Task RunIceRestartInterop(BrowserEngine engine)
    {
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(InteropNetwork.LocalIPv4(), 0),
            AudioCodecs = ["opus"],
        });
        await using var peer = client.CreatePeer();

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += (_, s) =>
        {
            if (s == PeerConnectionState.Connected) connected.TrySetResult();
        };

        // Echo the browser's audio back so both directions carry media across the restart.
        var inboundFrames = 0;
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Audio) return;
            track.FrameReceived += (_, frame) =>
            {
                Interlocked.Increment(ref inboundFrames);
                _ = peer.SendAudioAsync(frame.Payload.ToArray());
            };
        };

        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(await LoadPeerHtmlAsync());
        await bridge.StartAsync();

        var browserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Each answer the browser produces, in order: [0] the initial one, [1] its answer to the restart offer.
        var answers = Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            await foreach (var msg in bridge.Inbound.Reader.ReadAllAsync())
            {
                switch (msg.Type)
                {
                    case "ready": browserReady.TrySetResult(); break;
                    case "answer":
                        await peer.SetRemoteDescriptionAsync(msg.Sdp!);
                        await answers.Writer.WriteAsync(msg.Sdp!);
                        break;
                    case "candidate": await peer.AddIceCandidateAsync(msg.Candidate!); break;
                    case "stats": LastStats = msg; break;
                    case "log": break;
                }
            }
        });

        await using var browser = new BrowserPeer(engine);
        await browser.NavigateAsync(bridge.BaseUri);
        await browserReady.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = offer });
        _ = Task.Run(async () =>
        {
            await foreach (var c in pendingCandidates.Reader.ReadAllAsync())
                await bridge.SendAsync(new BridgeMessage { Type = "candidate", Candidate = c });
        });

        var firstAnswer = await answers.Reader.ReadAsync(Timeout(15));
        await peer.StartAsync();

        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"SDK-Peer wurde nicht Connected. Browser-Logs:\n  {browser.DumpLogs()}");
        }

        await WaitUntilAsync(
            () => Volatile.Read(ref inboundFrames) >= 20 && (LastStats?.BytesReceived ?? 0) > 0,
            TimeSpan.FromSeconds(20));
        var bytesBeforeRestart = LastStats?.BytesReceived ?? 0;
        Assert.True(bytesBeforeRestart > 0, $"Vor dem Restart floss keine Medien. Browser-Logs:\n  {browser.DumpLogs()}");

        // ── the restart ─────────────────────────────────────────────────────────
        var restartOffer = await peer.CreateIceRestartOfferAsync();
        Assert.NotEqual(IceUfragOf(offer), IceUfragOf(restartOffer));
        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = restartOffer });

        var restartAnswer = await answers.Reader.ReadAsync(Timeout(20));

        // THE assertion: the browser rotated its own ice-ufrag. Per RFC 8445 §9.1.1.1 an answerer does that only
        // when it has understood the offer as an ICE restart — so this is a foreign implementation confirming
        // that what we emit is a restart, which no loopback test can establish.
        Assert.NotEqual(IceUfragOf(firstAnswer), IceUfragOf(restartAnswer));

        // And the session survived it: media keeps arriving at the browser over the re-selected path, decrypted
        // by the DTLS session established before the restart — no second handshake, no rebuild.
        var framesBefore = Volatile.Read(ref inboundFrames);
        var grew = await WaitUntilAsync(
            () => (LastStats?.BytesReceived ?? 0) > bytesBeforeRestart && Volatile.Read(ref inboundFrames) > framesBefore,
            TimeSpan.FromSeconds(25));
        Assert.True(grew,
            $"Nach dem ICE-Restart floss keine Medien mehr: bytesReceived {bytesBeforeRestart} → " +
            $"{LastStats?.BytesReceived ?? 0}, Frames {framesBefore} → {Volatile.Read(ref inboundFrames)}. " +
            $"Browser-Logs:\n  {browser.DumpLogs()}");
    }

    private static CancellationToken Timeout(int seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(250);
        }

        return condition();
    }

    // The ice-ufrag of the first audio m-line (session-level a=ice-ufrag is used when the m-line has none).
    private static string IceUfragOf(string sdp)
    {
        var match = Regex.Match(sdp, @"^a=ice-ufrag:(?<u>\S+)", RegexOptions.Multiline);
        Assert.True(match.Success, "SDP ohne a=ice-ufrag — der Restart-Vergleich wäre bedeutungslos.");
        return match.Groups["u"].Value;
    }

    private static async Task<string> LoadPeerHtmlAsync() =>
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "peer.html"));
}
