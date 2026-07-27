using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class WebRtcBrowserInteropTests
{
    private volatile BridgeMessage? _lastStats;
    private BridgeMessage? LastStats { get => _lastStats; set => _lastStats = value; }

    [BrowserRequiredFact]
    public async Task SdkOfferer_Connects_And_Exchanges_Audio_With_RealBrowser()
    {
        // 1. SDK-Peer (Offerer). LocalEndPoint measure-first: Loopback zuerst (beide Peers auf 127.0.0.1).
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AudioCodecs = ["opus"],
        });
        await using var peer = client.CreatePeer();

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += (_, s) =>
        {
            if (s == PeerConnectionState.Connected) connected.TrySetResult();
        };

        // Browser->SDK: RemoteTrack-Frames zählen UND jeden Frame an den Browser zurück-echoen (SDK->Browser).
        var inboundFrames = 0;
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Audio) return;
            track.FrameReceived += (_, frame) =>
            {
                Interlocked.Increment(ref inboundFrames);
                // Echo: die empfangene Opus-Payload zurücksenden (kein Encoder nötig).
                _ = peer.SendAudioAsync(frame.Payload.ToArray());
            };
        };

        // 2. Signaling-Bridge + Browser-Candidates puffern, bis der WS offen ist.
        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(await LoadPeerHtmlAsync());
        await bridge.StartAsync();

        // Bridge-Pump: Browser-Nachrichten an den SDK-Peer weiterreichen. Die Answer MUSS angewandt
        // sein, bevor StartAsync läuft (RFC: keine ICE/DTLS ohne Remote-Description) → answerApplied.
        // Auf "ready" warten stellt sicher, dass der Browser seinen ws.onmessage-Handler registriert hat,
        // bevor wir das Offer senden (sonst Race → Offer verpasst).
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

        // 3. Browser starten -> er lädt peer.html, öffnet WS, sendet "ready".
        await using var browser = new BrowserPeer();
        await browser.NavigateAsync(bridge.BaseUri);
        await browserReady.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // 4. Offer + gepufferte SDK-Candidates an den Browser; danach live weiter-trickeln.
        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = offer });
        _ = Task.Run(async () =>
        {
            await foreach (var c in pendingCandidates.Reader.ReadAllAsync())
                await bridge.SendAsync(new BridgeMessage { Type = "candidate", Candidate = c });
        });

        // Answer abwarten (der Browser antwortet auf das Offer), DANN ICE/DTLS starten.
        await answerApplied.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await peer.StartAsync();

        // 5. Assertions: SDK verbindet, Browser->SDK-Frames fließen, SDK->Browser-Bytes kommen an.
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"SDK-Peer wurde nicht Connected. Browser-Logs:\n  {browser.DumpLogs()}");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline &&
               (Volatile.Read(ref inboundFrames) < 20 || (LastStats?.BytesReceived ?? 0) <= 0))
            await Task.Delay(250);

        Assert.True(Volatile.Read(ref inboundFrames) >= 20,
            $"Browser->SDK: nur {Volatile.Read(ref inboundFrames)} Audio-Frames empfangen. Browser-Logs:\n  {browser.DumpLogs()}");
        Assert.True((LastStats?.BytesReceived ?? 0) > 0,
            $"SDK->Browser: Browser meldete bytesReceived={LastStats?.BytesReceived?.ToString() ?? "null"}. Browser-Logs:\n  {browser.DumpLogs()}");
    }

    private static async Task<string> LoadPeerHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer.html");
        return await File.ReadAllTextAsync(path);
    }
}
