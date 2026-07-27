# WebRTC-Browser-Interop-Nachweis (GA-Gate Paket 1) — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Beweisen, dass die `CalloraVoipSdk.WebRtc`-Fassade mit einem echten headless-Chrome interoperiert — Connect (ICE/DTLS/SRTP) + bidirektionales Opus-Audio, SDK-Offerer ↔ Browser-Answerer.

**Architecture:** Neues isoliertes Testprojekt `CalloraVoipSdk.BrowserInteropTests` (Playwright .NET treibt headless Chromium). Ein in-process `BrowserInteropSignalingBridge` (HttpListener: HTTP für die Browser-Seite + WS für Offer/Answer/ICE) verbindet den SDK-Peer (C#) mit einer `peer.html` (RTCPeerConnection-Answerer, Chrome-fake-device Opus). Media beidseitig über ein Echo-Muster (SDK spielt empfangene Opus-Frames zurück) — kein Encoder nötig.

**Tech Stack:** .NET 10, xUnit 2.4.2, `Microsoft.Playwright`, headless Chromium (Playwright-Cache), `System.Net.HttpListener` + `System.Net.WebSockets`. measure-first gegen echten Browser.

**Spec:** `docs/audit/2026-07-27-webrtc-browser-interop-design.md`

**Verifizierte API-Fakten (gegen main `07c21be`):**
- `new WebRtcClient(new WebRtcConfiguration { LocalEndPoint = IPEndPoint, AudioCodecs = ["opus"] })` → `client.CreatePeer()` → `IPeerConnection : IAsyncDisposable`.
- `IPeerConnection`: `string CreateOffer()` · `Task<string> SetRemoteDescriptionAsync(string,ct)` · `Task AddIceCandidateAsync(string,ct)` · `Task GatherCandidatesAsync(ct)` · `Task StartAsync(ct)` · `ValueTask SendAudioAsync(ReadOnlyMemory<byte>,ct)` · `event EventHandler<PeerConnectionState> ConnectionStateChanged` · `event EventHandler<RemoteTrack> TrackReceived` · `event EventHandler<string> LocalIceCandidateDiscovered`.
- `RemoteTrack.FrameReceived (EncodedFrame)`; `EncodedFrame.Payload (ReadOnlyMemory<byte>)`, `.Kind`. `PeerConnectionState`: New/Connecting/**Connected**/Disconnected/Failed/Closed. `RemoteTrack.Kind` ist `TrackKind` (Audio/Video).
- Playwright-Chromium liegt unter `~/.cache/ms-playwright/chromium-*/chrome-linux64/chrome`.

**Verhaltensbewahrend:** Keine `src/`-Änderung. Alle neuen Tests tragen `[Trait("Category","BrowserInterop")]` → aus allen Nicht-Browser-CI-Jobs ausgeschlossen (Task 6).

## Datei-Struktur

- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/CalloraVoipSdk.BrowserInteropTests.csproj`
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserRequiredFactAttribute.cs` — Skip-Gate (Chromium vorhanden?)
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserInteropSignalingBridge.cs` — HttpListener HTTP+WS-Bridge
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/peer.html` — RTCPeerConnection-Answerer (Copy-to-output)
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserPeer.cs` — Playwright-Chromium-Wrapper
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/PlaywrightLaunchSmokeTests.cs` — Toolchain-Spike (Task 1)
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/SignalingBridgeTests.cs` — Bridge-Unit-Test (Task 2)
- Neu: `tests/CalloraVoipSdk.BrowserInteropTests/WebRtcBrowserInteropTests.cs` — der E2E-Nachweis (Task 5)
- Modify: `CalloraVoipSdk.sln` — Projekt hinzufügen
- Modify: `.github/workflows/ci.yml`, `.github/workflows/packages.yml` — `&Category!=BrowserInterop` (Task 6)
- Modify: `docs/audit/INTEROP_SOAK_AUDIT.md` — Coverage-Notiz (Task 6)

---

### Task 1: Projekt + Playwright-Toolchain-Spike

**Files:**
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/CalloraVoipSdk.BrowserInteropTests.csproj`
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserRequiredFactAttribute.cs`
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/PlaywrightLaunchSmokeTests.cs`
- Modify: `CalloraVoipSdk.sln`

**Kontext:** Das riskanteste Stück ist die Toolchain (Playwright .NET findet + startet das installierte headless Chromium). measure-first: erst das beweisen, bevor Bridge/Test gebaut werden. `BrowserRequiredFactAttribute` probt den Chromium-Pfad (analog `DockerRequiredFactAttribute`) und liefert den `ExecutablePath` — so entkoppeln wir von der Playwright-Browser-Install-Kopplung.

- [ ] **Step 1: csproj schreiben**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.6.0" />
    <PackageReference Include="xunit" Version="2.4.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.4.5" />
    <PackageReference Include="Microsoft.Playwright" Version="1.49.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Client\CalloraVoipSdk.Client.csproj" />
    <ProjectReference Include="..\..\src\Core\CalloraVoipSdk.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="peer.html" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```
Hinweis: `net10.0` only (Playwright + Browser-Interop braucht keine Multi-Target-Matrix). Falls `Microsoft.Playwright 1.49.0` nicht restore-bar ist, in Step 2 die neueste verfügbare Version nehmen (`dotnet add package Microsoft.Playwright`) — die exakte Version ist unkritisch, weil wir `ExecutablePath` explizit setzen.

- [ ] **Step 2: `BrowserRequiredFactAttribute` schreiben**

```csharp
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Ein <see cref="FactAttribute"/>, das den Test überspringt, wenn kein Playwright-Chromium auffindbar
/// ist (analog DockerRequiredFact). Exponiert den gefundenen Browser-Pfad für den Playwright-Launch.
/// </summary>
public sealed class BrowserRequiredFactAttribute : FactAttribute
{
    /// <summary>Der aufgelöste Chromium-Executable-Pfad, oder null wenn keiner gefunden wurde.</summary>
    public static readonly string? ChromiumPath = ResolveChromium();

    public BrowserRequiredFactAttribute()
    {
        if (ChromiumPath is null)
            Skip = "Kein Playwright-Chromium gefunden (~/.cache/ms-playwright/chromium-*) — Browser-Interop-Test übersprungen.";
    }

    private static string? ResolveChromium()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".cache", "ms-playwright");
        if (!Directory.Exists(root)) return null;
        // Neueste chromium-<rev>/chrome-linux64/chrome (höchste Revision zuerst).
        foreach (var dir in Directory.GetDirectories(root, "chromium-*")
                     .OrderByDescending(d => d))
        {
            var exe = Path.Combine(dir, "chrome-linux64", "chrome");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }
}
```

- [ ] **Step 3: Toolchain-Spike-Test schreiben**

```csharp
using Microsoft.Playwright;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class PlaywrightLaunchSmokeTests
{
    [BrowserRequiredFact]
    public async Task Chromium_Launches_Headless_And_Loads_Blank_Page()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = BrowserRequiredFactAttribute.ChromiumPath,
        });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<html><body><h1 id='t'>ok</h1></body></html>");
        var text = await page.InnerTextAsync("#t");
        Assert.Equal("ok", text);
    }
}
```

- [ ] **Step 4: Projekt in die .sln + build**

Run:
```bash
dotnet sln CalloraVoipSdk.sln add tests/CalloraVoipSdk.BrowserInteropTests/CalloraVoipSdk.BrowserInteropTests.csproj
dotnet build tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --nologo
```
Expected: Restore + Build 0 Fehler. Falls `Microsoft.Playwright 1.49.0` fehlt → `dotnet add tests/CalloraVoipSdk.BrowserInteropTests package Microsoft.Playwright` (neueste), erneut bauen.

- [ ] **Step 5: Spike gegen echtes Chromium ausführen (measure-first)**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "FullyQualifiedName~Chromium_Launches" --nologo`
Expected: PASS. **Falls FAIL:** Playwright braucht ggf. seine nativen Deps. Prüfen: (a) `ExecutablePath` zeigt auf eine existierende Datei (`BrowserRequiredFactAttribute.ChromiumPath` loggen); (b) fehlende Shared-Libs → `ldd <chromium>` zeigt fehlende `.so`. Falls Playwright den Browser trotz ExecutablePath nicht startet, alternativ `playwright.Chromium.LaunchAsync` OHNE ExecutablePath (nutzt Playwrights eigene Auflösung via `PLAYWRIGHT_BROWSERS_PATH`); Env `PLAYWRIGHT_BROWSERS_PATH=$HOME/.cache/ms-playwright` setzen. NICHT die Assertion schwächen — die Toolchain MUSS laufen, sonst BLOCKED melden mit der exakten Fehlermeldung.

- [ ] **Step 6: Commit**

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/CalloraVoipSdk.BrowserInteropTests.csproj tests/CalloraVoipSdk.BrowserInteropTests/BrowserRequiredFactAttribute.cs tests/CalloraVoipSdk.BrowserInteropTests/PlaywrightLaunchSmokeTests.cs CalloraVoipSdk.sln
git commit -m "test(webrtc): BrowserInteropTests-Projekt + Playwright-Chromium-Toolchain-Spike

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `BrowserInteropSignalingBridge` (HttpListener HTTP+WS)

**Files:**
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserInteropSignalingBridge.cs`
- Test: `tests/CalloraVoipSdk.BrowserInteropTests/SignalingBridgeTests.cs`

**Kontext:** Der einzige neue Infra-Baustein. Ein `HttpListener` auf `http://127.0.0.1:<port>/`: `GET /` liefert die `peer.html`, `GET /ws` upgraded auf WebSocket. Über den WS fließen JSON-Zeilen `{type, sdp?, candidate?, bytesReceived?, packetsReceived?}`. Der Bridge exponiert: ein `Task<...>`-basiertes API zum Senden an den Browser + ein `event`/`Channel` für empfangene Nachrichten. Design: eine `System.Threading.Channels.Channel<BridgeMessage>` für Inbound (Browser→C#) + `SendAsync(BridgeMessage)` für Outbound (C#→Browser).

- [ ] **Step 1: Failing Test schreiben (Bridge serviert HTML + WS-Echo)**

```csharp
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class SignalingBridgeTests
{
    [Fact]
    public async Task Bridge_Serves_Html_And_Roundtrips_A_Ws_Message()
    {
        await using var bridge = new BrowserInteropSignalingBridge(htmlBody: "<html>hello-bridge</html>");
        await bridge.StartAsync();

        // (a) GET / liefert das HTML
        using var http = new HttpClient();
        var html = await http.GetStringAsync(bridge.BaseUri);
        Assert.Contains("hello-bridge", html);

        // (b) WS: Client verbindet, C# empfängt eine Inbound-Nachricht, sendet eine Outbound zurück
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(bridge.WebSocketUri), CancellationToken.None);
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"ready\"}");
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

        var inbound = await bridge.Inbound.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ready", inbound.Type);

        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = "v=0..." });
        var buf = new byte[4096];
        var recv = await ws.ReceiveAsync(buf, CancellationToken.None);
        var text = Encoding.UTF8.GetString(buf, 0, recv.Count);
        Assert.Contains("\"type\":\"offer\"", text);
    }
}
```

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "FullyQualifiedName~Bridge_Serves_Html" --nologo`
Expected: FAIL — `BrowserInteropSignalingBridge` existiert nicht.

- [ ] **Step 3: Bridge implementieren**

```csharp
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>Eine Signaling-Nachricht zwischen C#-Test und Browser (JSON über den WebSocket).</summary>
public sealed class BridgeMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("sdp")] public string? Sdp { get; set; }
    [JsonPropertyName("candidate")] public string? Candidate { get; set; }
    [JsonPropertyName("bytesReceived")] public long? BytesReceived { get; set; }
    [JsonPropertyName("packetsReceived")] public long? PacketsReceived { get; set; }
}

/// <summary>
/// In-process HTTP+WS-Signaling-Bridge: serviert die Browser-Seite unter <c>/</c> und tauscht
/// Offer/Answer/ICE/Stats über einen WebSocket unter <c>/ws</c>. Inbound (Browser→C#) landet in
/// <see cref="Inbound"/>; Outbound (C#→Browser) via <see cref="SendAsync"/>.
/// </summary>
public sealed class BrowserInteropSignalingBridge : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpListener _listener = new();
    private readonly string _htmlBody;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private WebSocket? _socket;
    private readonly TaskCompletionSource _socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _accept;

    public Channel<BridgeMessage> Inbound { get; } = System.Threading.Channels.Channel.CreateUnbounded<BridgeMessage>();

    public BrowserInteropSignalingBridge(string htmlBody)
    {
        _htmlBody = htmlBody;
        _port = FreeTcpPort();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public string BaseUri => $"http://127.0.0.1:{_port}/";
    public string WebSocketUri => $"ws://127.0.0.1:{_port}/ws";

    /// <summary>Wartet, bis der Browser den WebSocket geöffnet hat (nach StartAsync + Navigation).</summary>
    public Task WebSocketConnected => _socketReady.Task;

    public Task StartAsync()
    {
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }

            if (ctx.Request.Url?.AbsolutePath == "/ws" && ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                _socket = wsCtx.WebSocket;
                _socketReady.TrySetResult();
                _ = Task.Run(() => ReceiveLoopAsync(_socket));
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(_htmlBody);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, _cts.Token).ConfigureAwait(false); }
            catch { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            var text = Encoding.UTF8.GetString(buf, 0, r.Count);
            var msg = JsonSerializer.Deserialize<BridgeMessage>(text, Json);
            if (msg is not null) await Inbound.Writer.WriteAsync(msg).ConfigureAwait(false);
        }
        Inbound.Writer.TryComplete();
    }

    public async Task SendAsync(BridgeMessage message)
    {
        var ws = _socket ?? throw new InvalidOperationException("WebSocket noch nicht verbunden.");
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
    }

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _socket?.Dispose(); } catch { /* best effort */ }
        try { _listener.Stop(); } catch { /* best effort */ }
        if (_accept is not null) { try { await _accept.ConfigureAwait(false); } catch { /* best effort */ } }
        _cts.Dispose();
    }
}
```

- [ ] **Step 4: Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "FullyQualifiedName~Bridge_Serves_Html" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/BrowserInteropSignalingBridge.cs tests/CalloraVoipSdk.BrowserInteropTests/SignalingBridgeTests.cs
git commit -m "test(webrtc): BrowserInteropSignalingBridge (HttpListener HTTP+WS) + Unit-Test

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `peer.html` (RTCPeerConnection-Answerer + fake-device + getStats)

**Files:**
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/peer.html`

**Kontext:** Die Browser-Seite. Öffnet den WS zur Bridge, wartet auf das `offer`, fügt seinen fake-device-Audio-Track hinzu, erzeugt das `answer`, trickelt ICE, und meldet periodisch `inbound-rtp`-Stats. Kein Test hier (wird in Task 5 gegen das SDK integriert); die Datei wird von der csproj in den Output kopiert und von der Bridge serviert. Der Nachrichten-Vertrag MUSS exakt zu `BridgeMessage` (Task 2) passen: `{type:"offer"|"answer"|"candidate"|"stats", sdp?, candidate?, bytesReceived?, packetsReceived?}`.

- [ ] **Step 1: `peer.html` schreiben**

```html
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"><title>Callora Browser Interop Peer</title></head>
<body>
<h1 id="status">init</h1>
<script>
(async () => {
  const set = (s) => { document.getElementById('status').textContent = s; };
  // WS zur Signaling-Bridge (dieselbe Origin, Pfad /ws).
  const ws = new WebSocket(`ws://${location.host}/ws`);
  const pc = new RTCPeerConnection({ iceServers: [] });   // host-only ICE, kein STUN

  // fake-device Audio (Chrome-Flag --use-fake-device-for-media-stream liefert einen Sinuston).
  const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
  for (const track of stream.getTracks()) pc.addTrack(track, stream);

  pc.onicecandidate = (e) => {
    if (e.candidate) ws.send(JSON.stringify({ type: 'candidate', candidate: e.candidate.candidate }));
  };
  pc.onconnectionstatechange = () => set('pc:' + pc.connectionState);

  ws.onmessage = async (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.type === 'offer') {
      await pc.setRemoteDescription({ type: 'offer', sdp: msg.sdp });
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      ws.send(JSON.stringify({ type: 'answer', sdp: answer.sdp }));
    } else if (msg.type === 'candidate') {
      try { await pc.addIceCandidate({ candidate: msg.candidate, sdpMLineIndex: 0 }); } catch (e) {}
    }
  };

  ws.onopen = () => {
    set('ws:open');
    ws.send(JSON.stringify({ type: 'ready' }));
    // Periodisch inbound-rtp (audio) melden — der Nachweis für SDK→Browser-Media.
    setInterval(async () => {
      const stats = await pc.getStats();
      stats.forEach((r) => {
        if (r.type === 'inbound-rtp' && r.kind === 'audio') {
          ws.send(JSON.stringify({
            type: 'stats',
            bytesReceived: r.bytesReceived || 0,
            packetsReceived: r.packetsReceived || 0,
          }));
        }
      });
    }, 500);
  };
})();
</script>
</body>
</html>
```

- [ ] **Step 2: Build (kopiert peer.html in den Output)**

Run: `dotnet build tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --nologo && ls tests/CalloraVoipSdk.BrowserInteropTests/bin/Release/net10.0/peer.html`
Expected: Build 0 Fehler; `peer.html` liegt im Output (CopyToOutputDirectory).

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/peer.html
git commit -m "test(webrtc): peer.html — RTCPeerConnection-Answerer (fake-device Opus, getStats-Report)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `BrowserPeer` (Playwright-Chromium-Wrapper)

**Files:**
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserPeer.cs`

**Kontext:** Startet headless Chromium mit den fake-media- + no-mDNS-Flags und navigiert zur Bridge-URL, sodass `peer.html` läuft. `IAsyncDisposable` räumt Browser/Playwright ab. Kein eigener Test (wird in Task 5 genutzt); die Kompilierung wird in Task 5 Step 3 mit-verifiziert.

- [ ] **Step 1: `BrowserPeer` schreiben**

```csharp
using Microsoft.Playwright;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Ein echter headless-Chromium-Peer (Playwright), der die von der Signaling-Bridge servierte
/// <c>peer.html</c> lädt und als WebRTC-Answerer gegen die SDK-Fassade connectet. Startet mit
/// synthetischer Media (fake-device) und deaktiviertem mDNS (echte host-IP-Candidates).
/// </summary>
public sealed class BrowserPeer : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task NavigateAsync(string url)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = BrowserRequiredFactAttribute.ChromiumPath,
            Args =
            [
                "--use-fake-device-for-media-stream",   // synthetischer Audio/Video-Stream (kein Mikrofon)
                "--use-fake-ui-for-media-stream",       // getUserMedia auto-grant
                "--disable-features=WebRtcHideLocalIpsWithMdns", // echte host-IPs statt .local (SDK droppt .local)
                "--autoplay-policy=no-user-gesture-required",
            ],
        });
        var page = await _browser.NewPageAsync();
        await page.GotoAsync(url);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) { try { await _browser.DisposeAsync(); } catch { /* best effort */ } }
        _playwright?.Dispose();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true --nologo`
Expected: 0 Warnungen / 0 Fehler.

- [ ] **Step 3: Commit**

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/BrowserPeer.cs
git commit -m "test(webrtc): BrowserPeer — Playwright-Chromium-Wrapper (fake-device, no-mDNS)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Der E2E-Browser-Interop-Nachweis (measure-first)

**Files:**
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/WebRtcBrowserInteropTests.cs`

**Kontext:** Der Kern. Baut den SDK-Peer (Offerer), verdrahtet ihn an die Bridge, startet den Browser, und beweist: SDK verbindet + Browser→SDK-Audio (RemoteTrack-Frames) + SDK→Browser-Audio (Browser-getStats), via Echo. **Das ist der measure-first-Task** — die ICE/DTLS/Media-Kette gegen einen echten Browser wird hier iteriert.

- [ ] **Step 1: Test schreiben**

```csharp
using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class WebRtcBrowserInteropTests
{
    [BrowserRequiredFact]
    public async Task SdkOfferer_Connects_And_Exchanges_Audio_With_RealBrowser()
    {
        // 1. SDK-Peer (Offerer). LocalEndPoint measure-first: Loopback zuerst (beide Peers auf 127.0.0.1).
        using var client = new WebRtcClient(new WebRtcConfiguration
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

        // Browser→SDK: RemoteTrack-Frames zählen UND jeden Frame an den Browser zurück-echoen (SDK→Browser).
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
        var pendingCandidates = System.Threading.Channels.Channel.CreateUnbounded<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => pendingCandidates.Writer.TryWrite(c);

        var offer = peer.CreateOffer();
        await using var bridge = new BrowserInteropSignalingBridge(await LoadPeerHtmlAsync());
        await bridge.StartAsync();

        // Bridge-Pump: Browser-Nachrichten an den SDK-Peer weiterreichen.
        var pump = Task.Run(async () =>
        {
            await foreach (var msg in bridge.Inbound.Reader.ReadAllAsync())
            {
                switch (msg.Type)
                {
                    case "answer": await peer.SetRemoteDescriptionAsync(msg.Sdp!); break;
                    case "candidate": await peer.AddIceCandidateAsync(msg.Candidate!); break;
                    case "stats": LastStats = msg; break;
                }
            }
        });

        // 3. Browser starten → er lädt peer.html, öffnet WS, sendet "ready".
        await using var browser = new BrowserPeer();
        await browser.NavigateAsync(bridge.BaseUri);
        await bridge.WebSocketConnected.WaitAsync(TimeSpan.FromSeconds(15));

        // 4. Offer + gepufferte SDK-Candidates an den Browser; danach live weiter-trickeln.
        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = offer });
        _ = Task.Run(async () =>
        {
            await foreach (var c in pendingCandidates.Reader.ReadAllAsync())
                await bridge.SendAsync(new BridgeMessage { Type = "candidate", Candidate = c });
        });

        await peer.StartAsync();

        // 5. Assertions: SDK verbindet, Browser→SDK-Frames fließen, SDK→Browser-Bytes kommen an.
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline &&
               (Volatile.Read(ref inboundFrames) < 20 || (LastStats?.BytesReceived ?? 0) <= 0))
            await Task.Delay(250);

        Assert.True(Volatile.Read(ref inboundFrames) >= 20,
            $"Browser→SDK: nur {Volatile.Read(ref inboundFrames)} Audio-Frames empfangen.");
        Assert.True((LastStats?.BytesReceived ?? 0) > 0,
            $"SDK→Browser: Browser meldete bytesReceived={LastStats?.BytesReceived?.ToString() ?? "null"}.");
    }

    private volatile BridgeMessage? _lastStats;
    private BridgeMessage? LastStats { get => _lastStats; set => _lastStats = value; }

    private static async Task<string> LoadPeerHtmlAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer.html");
        return await File.ReadAllTextAsync(path);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true --nologo`
Expected: 0 Warnungen / 0 Fehler. (Falls `TrackKind` einen anderen Namespace hat, per `grep -rn "enum TrackKind" src/` finden und den `using` ergänzen.)

- [ ] **Step 3: Gegen echten Browser ausführen (measure-first — hier wird iteriert)**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "FullyQualifiedName~SdkOfferer_Connects" --nologo`
Expected: PASS. **measure-first-Iteration bei FAIL (NIE eine Assertion schwächen — die Kette MUSS gegen den echten Browser laufen):**
- **Nie Connected:** ICE-Candidate-Mismatch. Prüfen, welche Candidates beide Seiten haben (SDK: `LocalIceCandidateDiscovered` loggen; Browser: `peer.html` um ein `ws.send({type:'log',...})` in `onicecandidate` ergänzen und in der Bridge loggen). Wahrscheinlichste Ursache: **LocalEndPoint**. Wenn der Browser keinen `127.0.0.1`-host-Candidate schickt (Chromium filtert loopback teils), den SDK auf die echte lokale LAN-IP binden: `LocalEndPoint = new IPEndPoint(<primäre NIC-IP>, 0)` (via `Dns.GetHostAddresses(Dns.GetHostName())` die erste IPv4). Dann matchen LAN-IP↔LAN-IP. Zweite mögliche Ursache: mDNS trotz Flag — verifizieren, dass die Browser-Candidates echte IPs (keine `.local`) enthalten; sonst Flag-Schreibweise prüfen.
- **Connected, aber 0 inbound-Frames:** Der Browser-Audio-Track wird nicht gesendet/negoziiert. Prüfen, dass `pc.addTrack` VOR `createAnswer` läuft (tut es in peer.html) und der SDK-Offer sendrecv-Audio anbietet. Ggf. `getUserMedia`-Fehler in der Seite (Flag `--use-fake-ui-for-media-stream` gesetzt?).
- **inbound-Frames OK, aber Browser bytesReceived=0:** Das Echo greift nicht. Prüfen, dass `SendAudioAsync` nach `Connected` feuert (vorher wird Media bis zum DTLS-Keying unterdrückt — das Echo startet automatisch mit dem ersten empfangenen Frame, also nach Connect). Ggf. Opus-Payload-Format: der Echo schickt die rohe empfangene Payload zurück — sollte valide sein. Falls nicht, die getStats-Deadline erhöhen (Browser-Stats brauchen ein paar Zyklen).
- **Flakes/Timing:** Deadlines großzügiger; headless-ICE + DTLS + erste RTCP-Zyklen brauchen unter Last Zeit.

- [ ] **Step 4: 2× wiederholen (Stabilität)**

Run: den Test-Befehl aus Step 3 noch 2×.
Expected: beide grün. Notiere etwaige Flakes (Deadline-Kandidaten).

- [ ] **Step 5: Commit**

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/WebRtcBrowserInteropTests.cs
git commit -m "test(webrtc): E2E Browser-Interop-Nachweis — SDK↔echter Chrome, connect + bidir Opus (Echo)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
(Falls Task 5 eine Anpassung an `peer.html` oder `BrowserPeer` brauchte, diese Dateien mit-stagen.)

---

### Task 6: CI-Ausschluss (lokal-first) + Register-Notiz

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/packages.yml`
- Modify: `docs/audit/INTEROP_SOAK_AUDIT.md`

**Kontext:** `BrowserInterop`-Tests brauchen Playwright-Chromium — nicht in den Standard-CI-Jobs vorhanden. Lokal-first: aus ALLEN Nicht-Browser-Jobs ausschließen (Lehre aus dem FreeSWITCH-CI-Bug: ein Docker/Browser-Test ohne Ausschluss-Trait rutscht in die falschen Jobs).

- [ ] **Step 1: Haupt-Test-Job + Release-Gate + Interop-Job filtern**

In `.github/workflows/ci.yml`:
- Haupt-Job (`Category!=SoakLong&Category!=Interop&Category!=InteropFreeSwitch&FullyQualifiedName!~ArchitectureTests`) → zusätzlich `&Category!=BrowserInterop`.
- Interop-Job (`Category=Interop&Category!=InteropLocalMedia&Category!=InteropFreeSwitch`) → bleibt (BrowserInterop ist nicht `Interop`, rutscht nicht rein — aber zur Sicherheit prüfen, dass kein BrowserInterop-Test zusätzlich `Interop` trägt; er tut es nicht).

In `.github/workflows/packages.yml` (Release-Gate `Category!=SoakLong&Category!=Interop&Category!=InteropFreeSwitch`) → zusätzlich `&Category!=BrowserInterop`.

- [ ] **Step 2: Discovery ohne Browser verifizieren**

Run:
```bash
dotnet build CalloraVoipSdk.sln -c Release --nologo 2>&1 | tail -3
dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "Category=BrowserInterop" --list-tests
```
Expected: Solution baut; alle 3 BrowserInterop-Tests (Chromium_Launches, Bridge_Serves_Html — Achtung: der Bridge-Unit-Test trägt `BrowserInterop`, ist aber Browser-frei; das ist ok, er läuft lokal — UND SdkOfferer_Connects) sind unter `Category=BrowserInterop` gelistet. Der Haupt-Job-Filter (`&Category!=BrowserInterop`) darf KEINEN davon listen (separat prüfen):
```bash
dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "Category!=SoakLong&Category!=Interop&Category!=InteropFreeSwitch&Category!=BrowserInterop" --list-tests
```
Expected: KEIN Test (leer) — alle BrowserInterop-Tests ausgeschlossen.

- [ ] **Step 3: Register-Notiz ergänzen**

In `docs/audit/INTEROP_SOAK_AUDIT.md` eine Coverage-Notiz „WebRTC-Browser-Interop (GA-Gate, Paket 1)" anfügen: erster Nachweis gegen echten headless-Chrome (Playwright), SDK-Offerer↔Browser-Answerer, connect + bidir Opus via Echo; lokal-first (`Category=BrowserInterop`); measure-first-Befund mDNS (SDK droppt `.local`, Test deaktiviert mDNS im Browser → **SDK-mDNS-Auflösung = separates GA-Item**); LocalEndPoint-Ergebnis (Loopback vs. LAN-IP, was tatsächlich verbunden hat).

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml .github/workflows/packages.yml docs/audit/INTEROP_SOAK_AUDIT.md
git commit -m "ci+docs(webrtc): BrowserInterop lokal-first aus allen Nicht-Browser-Jobs ausschließen + Register-Notiz

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Voll-Regression

**Files:** keine (nur Verifikation).

- [ ] **Step 1: BrowserInterop-Suite lokal grün**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --nologo`
Expected: alle Tests grün (Playwright-Spike + Bridge-Unit + E2E-Browser-Interop).

- [ ] **Step 2: Bestehende Suiten unberührt (Stichprobe)**

Run: `dotnet build CalloraVoipSdk.sln -c Release --nologo 2>&1 | tail -3`
Expected: Solution baut 0 Fehler (das neue Projekt bricht nichts).

- [ ] **Step 3: `src/` unberührt**

Run: `git diff --stat 07c21be..HEAD -- src/`
Expected: leer (reine Testinfra; etwaige SDK-mDNS-Auflösung ist ein SEPARATES GA-Item, nicht hier).

---

## Abschluss

Nach Task 7: der GA-Gate ist im Kern durch — die Fassade interoperiert nachgewiesen mit einem echten Browser (connect + bidir Audio). Branch per `superpowers:finishing-a-development-branch` abschließen (PR-Erstellung durch den User). **Durch den Nachweis motivierte/priorisierte Folge-Pakete (GA-Reifung):** SDK-mDNS-Candidate-Auflösung (falls der Test es als nötig zeigt) · Video (VP8) Browser-Interop · Browser-Offerer-Richtung · dann die übrigen GA-Blocker (Answerer-Relay-Gap, TCP/TLS-TURN-Config-Diagnostik, Video-Stats). Reihenfolge nach dem, was dieser erste Nachweis über den realen Browser-Interop-Zustand zeigt.
