# WebRTC VP8-Video-Browser-Interop (GA-Reifung Paket 3) — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Beweisen, dass die `CalloraVoipSdk.WebRtc`-Fassade VP8-Video bidirektional mit echtem headless Chrome interoperiert — mit Dekodier-Nachweis (`getStats framesDecoded > 0`).

**Architecture:** Erweiterung des Paket-1-Browser-Harness (`BrowserInteropTests`): neue `peer-video.html` (RTCPeerConnection-Answerer mit fake-device Video), `BridgeMessage` um `framesDecoded`, ein neuer `[BrowserRequiredFact]`-Test mit SDK `EnableVideo=true, VideoCodecs=["VP8"]` und keyframe-bewusstem Video-Echo.

**Tech Stack:** .NET 10, xUnit, Microsoft.Playwright, headless Chromium, VP8. measure-first gegen echten Browser.

**Spec:** `docs/audit/2026-07-27-webrtc-video-interop-design.md`

**Verifizierte API-Fakten (main + Paket 1):**
- `new WebRtcClient(new WebRtcConfiguration { LocalEndPoint, AudioCodecs=["opus"], EnableVideo=true, VideoCodecs=["VP8"] })`.
- `IPeerConnection.SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, ct)`.
- `TrackReceived (RemoteTrack)`; `RemoteTrack.Kind == TrackKind.Video`; `RemoteTrack.FrameReceived (EncodedFrame)`; `EncodedFrame.Payload (ReadOnlyMemory<byte>)`, `.RtpTimestamp (uint?)`, `.IsKeyFrame (bool)`.
- Paket-1-Harness: `BrowserInteropSignalingBridge` (`BridgeMessage` type/sdp/candidate/bytesReceived/packetsReceived), `BrowserPeer`, `BrowserRequiredFactAttribute`, `WebRtcBrowserInteropTests` (Muster: SDK-Offerer, Bridge-Pump ready/answer/candidate/stats/log, browserReady/answerApplied-Gates).

**Verhaltensbewahrend:** Der bestehende Paket-1-Audio-Test bleibt unberührt (neue `peer-video.html`, additive `BridgeMessage`-Erweiterung, neuer Test). Kategorie `BrowserInterop` (lokal-first). Keine `src/`-Änderung.

## Datei-Struktur

- Modify: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserInteropSignalingBridge.cs` — `BridgeMessage.FramesDecoded`.
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/peer-video.html` — RTCPeerConnection-Answerer mit fake-device Video, getStats(video).
- Modify: `tests/CalloraVoipSdk.BrowserInteropTests/CalloraVoipSdk.BrowserInteropTests.csproj` — `peer-video.html` in den Output kopieren.
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/WebRtcVideoBrowserInteropTests.cs` — der Video-Interop-Test.

---

### Task 1: `BridgeMessage.FramesDecoded` + `peer-video.html`

**Files:**
- Modify: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserInteropSignalingBridge.cs`
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/peer-video.html`
- Modify: `tests/CalloraVoipSdk.BrowserInteropTests/CalloraVoipSdk.BrowserInteropTests.csproj`

**Kontext:** Die Browser-Seite + das Datenmodell. `peer-video.html` spiegelt `peer.html`, aber mit `getUserMedia({video:true})` und einem `inbound-rtp`(kind=video)-getStats-Report, der `framesDecoded` mitmeldet. Nachrichten-Vertrag muss zu `BridgeMessage` passen.

- [ ] **Step 1: `BridgeMessage` um `FramesDecoded` erweitern**

In `BrowserInteropSignalingBridge.cs`, in der `BridgeMessage`-Klasse (nach `PacketsReceived`):
```csharp
    [JsonPropertyName("framesDecoded")] public long? FramesDecoded { get; set; }
```

- [ ] **Step 2: `peer-video.html` schreiben**

```html
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"><title>Callora Browser Interop Video Peer</title></head>
<body>
<h1 id="status">init</h1>
<script>
(async () => {
  const set = (s) => { document.getElementById('status').textContent = s; };
  const ws = new WebSocket(`ws://${location.host}/ws`);
  const send = (o) => { try { ws.send(JSON.stringify(o)); } catch (e) {} };
  const log = (m) => { console.log('[peer-video] ' + m); send({ type: 'log', candidate: String(m) }); };
  window.onerror = (m) => log('window.onerror: ' + m);

  const pc = new RTCPeerConnection({ iceServers: [] });   // host-only ICE, kein STUN
  pc.onicecandidate = (e) => { if (e.candidate) send({ type: 'candidate', candidate: e.candidate.candidate }); };
  pc.oniceconnectionstatechange = () => log('ice:' + pc.iceConnectionState);
  pc.onconnectionstatechange = () => { set('pc:' + pc.connectionState); log('pc:' + pc.connectionState); };

  ws.onmessage = async (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.type === 'offer') {
      log('offer received');
      await pc.setRemoteDescription({ type: 'offer', sdp: msg.sdp });
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      send({ type: 'answer', sdp: answer.sdp });
      log('answer sent');
    } else if (msg.type === 'candidate') {
      try { await pc.addIceCandidate({ candidate: msg.candidate, sdpMLineIndex: 0 }); }
      catch (e) { log('addIceCandidate failed: ' + e); }
    }
  };

  try {
    // fake-device Video (Chrome-Flag --use-fake-device-for-media-stream liefert ein Testpattern).
    const stream = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
    for (const track of stream.getTracks()) pc.addTrack(track, stream);
    log('getUserMedia video ok, tracks=' + stream.getTracks().length);
  } catch (e) {
    log('getUserMedia FAILED: ' + e);
  }

  const announceReady = () => {
    set('ws:open');
    send({ type: 'ready' });
    log('ready sent');
    setInterval(async () => {
      const stats = await pc.getStats();
      stats.forEach((r) => {
        if (r.type === 'inbound-rtp' && r.kind === 'video') {
          send({
            type: 'stats',
            bytesReceived: r.bytesReceived || 0,
            framesDecoded: r.framesDecoded || 0,
          });
        }
      });
    }, 500);
  };
  if (ws.readyState === WebSocket.OPEN) announceReady();
  else ws.onopen = announceReady;
})();
</script>
</body>
</html>
```

- [ ] **Step 3: `peer-video.html` in den Output kopieren**

In `CalloraVoipSdk.BrowserInteropTests.csproj`, im `<ItemGroup>` mit dem bestehenden `peer.html`-Eintrag:
```xml
    <None Update="peer-video.html" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 4: Build**

Run: `dotnet build tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true --nologo && ls tests/CalloraVoipSdk.BrowserInteropTests/bin/Release/net10.0/peer-video.html`
Expected: 0 Warnungen / 0 Fehler; `peer-video.html` liegt im Output.

- [ ] **Step 5: Commit**

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/BrowserInteropSignalingBridge.cs tests/CalloraVoipSdk.BrowserInteropTests/peer-video.html tests/CalloraVoipSdk.BrowserInteropTests/CalloraVoipSdk.BrowserInteropTests.csproj
git commit -m "test(webrtc): peer-video.html (fake-device Video, getStats framesDecoded) + BridgeMessage.FramesDecoded

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Video-Interop-Test (measure-first — trägt das VP8-Echo fürs Dekodieren?)

**Files:**
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/WebRtcVideoBrowserInteropTests.cs`

**Kontext:** Der Kern. Struktur wie `WebRtcBrowserInteropTests` (SDK-Offerer, Bridge-Pump, browserReady/answerApplied-Gates), aber Video statt Audio: SDK `EnableVideo=true, VideoCodecs=["VP8"]`, `TrackReceived` Kind=Video → **keyframe-bewusstes Echo** (`SendVideoFrameAsync` erst ab dem ersten `IsKeyFrame`), Assertions (a) Browser→SDK Video-Frames, (b) Browser bytesReceived>0, (c) Browser framesDecoded>0. **Das ist der measure-first-Task** — der Video-Echo-/Dekodier-Pfad wird hier gegen echtes Chrome iteriert.

- [ ] **Step 1: Test schreiben**

```csharp
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

        // 5. Assertions: connected + Browser→SDK-Video-Frames + Browser bytesReceived + framesDecoded.
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
```

- [ ] **Step 2: Build**

Run: `dotnet build tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true --nologo`
Expected: 0 Warnungen / 0 Fehler.

- [ ] **Step 3: Gegen echten Browser ausführen (measure-first — hier wird iteriert)**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "FullyQualifiedName~SdkOfferer_Exchanges_Vp8Video" --nologo --verbosity normal`
Expected: PASS — connected + ≥10 Browser→SDK-VP8-Frames + Browser bytesReceived>0 + **framesDecoded>0**.
**measure-first-Iteration bei FAIL (NIE eine Assertion schwächen; Fixes am Test/`peer-video.html`, nicht am SDK):**
- **Nie Connected:** wie Paket 1 (ICE host-only auf Loopback; die Audio-Variante verbindet, also ist der Signaling-Pfad bewiesen — Video ändert nur die m-Line). Browser-Logs im Assert-Dump prüfen.
- **inboundFrames=0 (kein Browser→SDK-Video):** `peer-video.html` `getUserMedia({video:true})` + `addTrack` VOR `createAnswer` (tut es). SDK-Offer muss eine Video-m-Line (VP8) anbieten (`EnableVideo=true`). Prüfen, dass der SDK die Browser-VP8-Frames als Video-Track surface’t (`TrackReceived` Kind=Video).
- **framesDecoded=0 trotz bytesReceived>0 (RTP kommt an, Browser dekodiert nicht):** das Keyframe-Problem. (i) Prüfen, dass `_videoEchoStarted` erst bei einem `IsKeyFrame`-Frame greift und der SDK-Depacketiser das Keyframe-Flag korrekt setzt (loggen: kam je ein `frame.IsKeyFrame==true`?). (ii) Falls der Browser ein Keyframe braucht, das er verpasst hat (PLI ignoriert), das erste Keyframe im Echo **periodisch wiederholen** (die gepufferte Keyframe-Payload alle ~1 s erneut senden, bis framesDecoded>0). (iii) **Fallback (dokumentiert, User-abgestimmt im Design §6):** wenn das Echo prinzipiell kein dekodierbares Keyframe liefert, die framesDecoded-Assertion auf einen `[Fact(Skip=...)]`/entfernen zurücknehmen und den Transport-Nachweis (Frames + bytesReceived) als Ergebnis behalten, mit klarer Register-Notiz zur Dekodier-Grenze. NICHT still schwächen — erst hier ankommen, wenn (i)+(ii) gemessen nicht tragen.

- [ ] **Step 4: 2× wiederholen (Stabilität)** und Commit

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/WebRtcVideoBrowserInteropTests.cs
git commit -m "test(webrtc): E2E VP8-Video-Interop — SDK↔echter Chrome, bidir + Dekodier-Nachweis (framesDecoded)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
(Falls Task 2 eine Anpassung an `peer-video.html` brauchte — z. B. Keyframe-Wiederholung —, diese mit-stagen.)

---

### Task 3: Regression + Register-Notiz

**Files:**
- Modify: `docs/audit/INTEROP_SOAK_AUDIT.md`

- [ ] **Step 1: Paket-1-Audio-Test unberührt + volle BrowserInterop-Suite grün**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --nologo`
Expected: alle grün (Playwright-Spike + Bridge-Unit + Paket-1-Audio-Interop + neuer Video-Interop). Der Audio-Test darf nicht regressiert sein.

- [ ] **Step 2: `src/` unberührt**

Run: `git diff --stat origin/main..HEAD -- src/`
Expected: leer (reine Testinfra).

- [ ] **Step 3: Register-Notiz ergänzen**

In `docs/audit/INTEROP_SOAK_AUDIT.md` eine Coverage-Notiz „WebRTC VP8-Video-Interop (GA-Reifung Paket 3)" anfügen: bidirektionaler VP8-Video-Nachweis gegen echten Chrome (SDK-Offerer, fake-device Video, Echo mit Keyframe-Bewusstsein), mit dem tatsächlich erreichten Assertion-Niveau (Dekodier-Nachweis `framesDecoded>0`, ODER — falls der measure-first-Task es zeigte — Transport-Nachweis mit dokumentierter Dekodier-Grenze). Lokal-first (`Category=BrowserInterop`).

- [ ] **Step 4: Commit**

```bash
git add docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "docs(audit): Coverage-Notiz VP8-Video-Browser-Interop (Paket 3)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Abschluss

Nach Task 3: die Fassade interoperiert nachgewiesen per VP8-Video mit echtem Chrome (bidirektional, mit Dekodier-Nachweis) — der Video-Kern des GA-Gate ist bewiesen. Branch `feat/webrtc-video-interop` per `superpowers:finishing-a-development-branch` abschließen (PR durch den User; Base=main, unabhängig von Paket 2). **Verbleibende GA-Reifung:** Browser-Offerer-Richtung · Answerer-Relay-Gap · TCP/TLS-TURN-Config-Diagnostik · Video-Stats-null (`WebRtcStats`-Video-Felder). Hinweis: reine Testinfra (tests/), daher kein `src/`-Analyzer-/ArchitectureTests-Gate-Risiko wie bei Paket 2 — nur Build + Tests grün.
