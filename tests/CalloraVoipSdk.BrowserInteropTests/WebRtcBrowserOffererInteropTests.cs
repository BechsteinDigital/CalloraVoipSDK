using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Gegenstück zu <see cref="WebRtcBrowserInteropTests"/>: hier ist der <b>Browser der Offerer</b>
/// und die SDK-Fassade der <b>Answerer</b>. Der Browser ruft <c>createOffer</c>; der SDK-Peer
/// nimmt die Offer über <see cref="IPeerConnection.SetRemoteDescriptionAsync"/> entgegen (ohne
/// vorheriges <c>CreateOffer</c>) und erzeugt die Answer als Rückgabewert — DTLS-Rolle
/// <c>active</c>, ICE-Rolle <c>controlled</c>. Beweist die Answerer-Richtung end-to-end.
/// </summary>
[Trait("Category", "BrowserInterop")]
public sealed class WebRtcBrowserOffererInteropTests
{
    private volatile BridgeMessage? _lastStats;
    private BridgeMessage? LastStats { get => _lastStats; set => _lastStats = value; }

    [BrowserRequiredFact]
    public async Task BrowserOfferer_Connects_And_Exchanges_Audio_With_SdkAnswerer()
    {
        // 1. SDK-Peer als ANSWERER — es wird bewusst KEIN CreateOffer() gerufen. Die Answerer-Rolle
        //    erkennt der Peer daran, dass keine lokale Offer aussteht.
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

        // Browser->SDK: RemoteTrack-Frames zählen UND jeden Frame zurück-echoen (SDK->Browser).
        var inboundFrames = 0;
        peer.TrackReceived += (_, track) =>
        {
            if (track.Kind != TrackKind.Audio) return;
            track.FrameReceived += (_, frame) =>
            {
                Interlocked.Increment(ref inboundFrames);
                _ = peer.SendAudioAsync(frame.Payload.ToArray());   // Echo, kein Encoder nötig
            };
        };

        // 2. Signaling-Bridge + Candidate-Puffer. Die lokalen Answerer-Candidates entstehen erst
        //    ab SetRemoteDescriptionAsync (dort bindet der Peer seinen Socket) → puffern.
        var pendingCandidates = Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        await using var bridge = new BrowserInteropSignalingBridge(await LoadOffererHtmlAsync());
        await bridge.StartAsync();

        // Bridge-Pump: das Browser-OFFER entgegennehmen -> SetRemoteDescriptionAsync liefert die
        // Answer zurück -> Answer an den Browser senden. Erst danach ICE/DTLS starten.
        var answerSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await foreach (var msg in bridge.Inbound.Reader.ReadAllAsync())
            {
                switch (msg.Type)
                {
                    case "offer":
                        var answer = await peer.SetRemoteDescriptionAsync(msg.Sdp!);
                        await bridge.SendAsync(new BridgeMessage { Type = "answer", Sdp = answer });
                        answerSent.TrySetResult();
                        break;
                    case "candidate": await peer.AddIceCandidateAsync(msg.Candidate!); break;
                    case "stats": LastStats = msg; break;
                    case "log": /* Browser-Diagnose kommt über page.Console */ break;
                }
            }
        });

        // 3. Browser starten -> er lädt peer-offerer.html, öffnet WS, sendet proaktiv das Offer.
        await using var browser = new BrowserPeer();
        await browser.NavigateAsync(bridge.BaseUri);

        // 4. Auf die eigene Answer warten (Offer verarbeitet), dann lokale Candidates trickeln + StartAsync.
        await answerSent.Task.WaitAsync(TimeSpan.FromSeconds(20));
        _ = Task.Run(async () =>
        {
            await foreach (var c in pendingCandidates.Reader.ReadAllAsync())
                await bridge.SendAsync(new BridgeMessage { Type = "candidate", Candidate = c });
        });
        await peer.StartAsync();

        // 5. Assertions: SDK verbindet als Answerer, Browser->SDK-Frames fließen, SDK->Browser-Bytes kommen an.
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"SDK-Answerer wurde nicht Connected. Browser-Logs:\n  {browser.DumpLogs()}");
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

    private static async Task<string> LoadOffererHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer-offerer.html");
        return await File.ReadAllTextAsync(path);
    }
}
