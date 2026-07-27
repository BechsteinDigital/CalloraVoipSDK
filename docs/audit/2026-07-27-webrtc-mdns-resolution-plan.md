# SDK-mDNS-Candidate-Auflösung (WebRTC GA-Reifung Paket 2) — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Der `WebRtcPeerConnection` löst empfangene `.local`-mDNS-ICE-Candidates auf (statt sie zu droppen), sodass die WebRTC-Fassade mit Default-Browsern (mDNS an) interoperiert.

**Architecture:** Neuer `IMdnsResolver`-Seam mit `SystemMdnsResolver` (`System.Net.Dns`) als Default (SIPSorcery-Muster). `WebRtcPeerConnection.AddIceCandidateAsync` löst bei einem `.local`-Namen im Hintergrund auf (fire-and-forget, peer-lifecycle-gebunden) und speist den aufgelösten Endpoint in denselben buffer/AddRemoteCandidate-Pfad. RFC-8828-Pflichtregeln (`uuid.local` genau-ein-Punkt, >1 IP → ignorieren).

**Tech Stack:** .NET 8/9/10, xUnit, `System.Net.Dns`. Verifikation: Unit-Tests (gemockter Resolver) + E2E gegen echten Chrome (Paket-1-Browser-Harness, mDNS aktiviert).

**Spec:** `docs/audit/2026-07-27-webrtc-mdns-resolution-design.md`

**Verifizierte Code-Fakten (main + Paket 1):**
- `WebRtcPeerConnection` ctor (`src/Core/Infrastructure/WebRtc/WebRtcPeerConnection.cs:112`): `(WebRtcPeerOptions options, ISdpOfferAnswerNegotiator negotiator, ISdpSessionParser parser, ISdpSessionSerializer serializer, IDtlsSrtpHandshaker handshaker, DtlsCertificate certificate, ILoggerFactory loggerFactory, IIceStunProbe? stunProbe = null, TurnAllocationProbe? turnProbe = null)`.
- `AddIceCandidateAsync` (Z. 495) → `ParseTrickleCandidate` (Z. 655) → `SdpIceCandidate.TryParse` + `IPAddress.TryParse(parsed.Address)`; buffer via `_pendingRemoteCandidates` (Z. 85, unter `_sync`) sonst `session.AddRemoteCandidate(endpoint, priority)`.
- `SdpIceCandidate` (`src/Core/Infrastructure/Sdp/Models/SdpIceCandidate.cs`): `Component` (int), `Transport` (string), `Priority` (long), `Address` (string), `Port` (int), `TryParse`.
- Neue Dateien nach `src/Core/Infrastructure/Common/Network/` (bestehende Resolver-Helper dort).
- Der Peer hat KEINEN Lifecycle-CT — dieser Plan führt `_mdnsLifetime` (CancellationTokenSource) ein, gecancelt in `DisposeAsync`.
- Paket-1-Harness: `tests/CalloraVoipSdk.BrowserInteropTests` (`BrowserPeer`, `BrowserInteropSignalingBridge`, `BridgeMessage`, `BrowserRequiredFactAttribute`).

**Verhaltensbewahrend:** Kein `.local` → byte-identischer Pfad. Auflösung scheitert/kein OS-mDNS → Candidate verworfen wie heute (kein Regress).

## Datei-Struktur

- Neu: `src/Core/Infrastructure/Common/Network/IMdnsResolver.cs` — Seam.
- Neu: `src/Core/Infrastructure/Common/Network/SystemMdnsResolver.cs` — Default-Impl (`System.Net.Dns` + RFC-Regeln).
- Modify: `src/Core/Infrastructure/WebRtc/WebRtcPeerConnection.cs` — ctor-Param, `_mdnsLifetime`, `AddIceCandidateAsync`-mDNS-Zweig, `DisposeAsync`-Cancel.
- Modify: `src/Core/Infrastructure/WebRtc/WebRtcSessionFactory.cs` (+ ggf. `WebRtcClient.cs`) — Default-Resolver durchreichen, optional über Options überschreibbar.
- Test: `tests/CalloraVoipSdk.Core.IntegrationTests/` — `SystemMdnsResolverTests.cs`, `WebRtcMdnsCandidateTests.cs`.
- Modify: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserPeer.cs` — mDNS-Flag parametrisierbar.
- Test: `tests/CalloraVoipSdk.BrowserInteropTests/MdnsResolutionSpikeTests.cs` (Task 1, danach ggf. entfernt) + Erweiterung von `WebRtcBrowserInteropTests.cs` (Task 5).

---

### Task 1: measure-first-Spike — löst der OS-Resolver echte Chromium-`.local`-Namen?

**Files:**
- Modify: `tests/CalloraVoipSdk.BrowserInteropTests/BrowserPeer.cs`
- Create: `tests/CalloraVoipSdk.BrowserInteropTests/MdnsResolutionSpikeTests.cs`

**Kontext:** Bevor die Seam-Maschinerie gebaut wird, das zentrale Risiko klären: löst `Dns.GetHostAddressesAsync` einen **ephemeren Chromium-`uuid.local`** (von Chromiums eigenem mDNS-Responder) auf diesem Host wirklich auf? Wir starten einen Browser mit mDNS AN, sammeln seine `.local`-Candidates über die bestehende Bridge und versuchen sie aufzulösen.

- [ ] **Step 1: `BrowserPeer` mDNS-Flag parametrisierbar machen**

In `BrowserPeer.NavigateAsync` die Args-Liste vom mDNS-Flag entkoppeln:
```csharp
    public async Task NavigateAsync(string url, bool disableMdns = true)
    {
        _playwright = await Playwright.CreateAsync();
        var args = new List<string>
        {
            "--use-fake-device-for-media-stream",
            "--use-fake-ui-for-media-stream",
            "--autoplay-policy=no-user-gesture-required",
        };
        if (disableMdns)
            args.Add("--disable-features=WebRtcHideLocalIpsWithMdns"); // echte host-IPs statt .local
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = BrowserRequiredFactAttribute.ChromiumPath,
            Args = args.ToArray(),
        });
        var page = await _browser.NewPageAsync();
        page.Console += (_, m) => Logs.Enqueue($"[console.{m.Type}] {m.Text}");
        page.PageError += (_, e) => Logs.Enqueue($"[pageerror] {e}");
        await page.GotoAsync(url);
    }
```

- [ ] **Step 2: Spike-Test schreiben**

```csharp
using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;
using Xunit.Abstractions;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class MdnsResolutionSpikeTests
{
    private readonly ITestOutputHelper _out;
    public MdnsResolutionSpikeTests(ITestOutputHelper output) => _out = output;

    [BrowserRequiredFact]
    public async Task OsResolver_Resolves_A_Real_Chromium_Mdns_Candidate()
    {
        // Minimaler SDK-Offerer nur um dem Browser ein Offer zu geben (er gathert dann Candidates).
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AudioCodecs = ["opus"],
        });
        await using var peer = client.CreatePeer();
        var offer = peer.CreateOffer();

        await using var bridge = new BrowserInteropSignalingBridge(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "peer.html")));
        await bridge.StartAsync();

        var mdnsNames = new List<string>();
        var gotOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browserReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await foreach (var msg in bridge.Inbound.Reader.ReadAllAsync())
            {
                if (msg.Type == "ready") browserReady.TrySetResult();
                if (msg.Type == "candidate" && msg.Candidate is { } c && c.Contains(".local", StringComparison.OrdinalIgnoreCase))
                {
                    // Address-Token aus "candidate:<foundation> <comp> <transport> <prio> <ADDRESS> <port> ..."
                    var parts = c.Split(' ');
                    if (parts.Length > 4) { mdnsNames.Add(parts[4]); gotOne.TrySetResult(); }
                }
            }
        });

        // Browser MIT mDNS starten (disableMdns: false).
        await using var browser = new BrowserPeer();
        await browser.NavigateAsync(bridge.BaseUri, disableMdns: false);
        await browserReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await bridge.SendAsync(new BridgeMessage { Type = "offer", Sdp = offer });

        await gotOne.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var name = mdnsNames[0];
        _out.WriteLine($"Chromium mDNS-Candidate-Name: {name}");

        // ★ Die eigentliche Machbarkeit: löst der OS-Resolver diesen ephemeren Namen auf?
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var addrs = await Dns.GetHostAddressesAsync(name, cts.Token);
            _out.WriteLine($"Aufgelöst zu: {string.Join(", ", addrs.Select(a => a.ToString()))}");
            Assert.NotEmpty(addrs);
        }
        catch (Exception ex)
        {
            Assert.Fail($"OS-Resolver löste den Chromium-mDNS-Namen '{name}' NICHT auf: {ex.GetType().Name}: {ex.Message}. " +
                        "→ Option A (System.Net.Dns) trägt auf diesem Host nicht; Resolver-Impl muss ein eigener Multicast-Query werden (Seam bleibt).");
        }
    }
}
```

- [ ] **Step 3: Spike ausführen (die Entscheidungsstelle)**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "FullyQualifiedName~OsResolver_Resolves" --nologo`
Expected: **PASS** — der Testausgabe entnehmen, dass der Chromium-`uuid.local`-Name zu einer IP aufgelöst wurde. **Falls FAIL:** Option A trägt auf diesem Host nicht. STOP und dem Controller melden: die Architektur (Seam) bleibt, aber die Default-Impl in Task 2 muss ein eigener Multicast-DNS-Query werden (RFC 6762) statt `System.Net.Dns`. NICHT weiterbauen, bis das geklärt ist.

- [ ] **Step 4: Commit** (Spike-Erkenntnis festhalten; der Spike-Test wird in Task 6 entfernt oder als Doku belassen)

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/BrowserPeer.cs tests/CalloraVoipSdk.BrowserInteropTests/MdnsResolutionSpikeTests.cs
git commit -m "test(webrtc): mDNS-Machbarkeits-Spike — OS-Resolver löst echten Chromium-.local-Candidate

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `IMdnsResolver` + `SystemMdnsResolver` (mit RFC-Regeln)

**Files:**
- Create: `src/Core/Infrastructure/Common/Network/IMdnsResolver.cs`
- Create: `src/Core/Infrastructure/Common/Network/SystemMdnsResolver.cs`
- Test: `tests/CalloraVoipSdk.Core.IntegrationTests/SystemMdnsResolverTests.cs`

**Kontext:** Der Seam + Default-Impl. `SystemMdnsResolver` wendet die RFC-8828-Regeln an, damit die Peer-Logik davon frei bleibt. Rein per Unit-Test prüfbar (keine echte mDNS-Query nötig für die Regel-Tests; die Namensvalidierung + Multi-IP-Filterung sind deterministisch über einen injizierten Lookup-Delegate).

- [ ] **Step 1: Failing Test schreiben**

```csharp
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SystemMdnsResolverTests
{
    private static SystemMdnsResolver WithLookup(Func<string, IPAddress[]> lookup)
        => new((host, ct) => Task.FromResult(lookup(host)));

    [Fact]
    public async Task Resolves_Single_Ip_For_Valid_Uuid_Local()
    {
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5")]);
        var ip = await resolver.ResolveAsync("abc123.local", CancellationToken.None);
        Assert.Equal(IPAddress.Parse("192.168.1.5"), ip);
    }

    [Fact]
    public async Task Returns_Null_When_Resolution_Yields_More_Than_One_Ip()
    {
        // RFC 8828 §3.2.2: SHOULD ignore candidates resolving to more than one IP.
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5"), IPAddress.Parse("192.168.1.6")]);
        Assert.Null(await resolver.ResolveAsync("abc123.local", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Null_For_Name_With_More_Than_One_Dot()
    {
        var resolver = WithLookup(_ => [IPAddress.Parse("192.168.1.5")]);
        Assert.Null(await resolver.ResolveAsync("evil.host.local", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Null_When_Lookup_Throws()
    {
        var resolver = new SystemMdnsResolver((_, _) => throw new System.Net.Sockets.SocketException());
        Assert.Null(await resolver.ResolveAsync("abc123.local", CancellationToken.None));
    }
}
```

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.Core.IntegrationTests -c Release -f net10.0 --filter "FullyQualifiedName~SystemMdnsResolverTests" --nologo`
Expected: FAIL — Typen existieren nicht.

- [ ] **Step 3: Seam + Impl schreiben**

`IMdnsResolver.cs`:
```csharp
using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Network;

/// <summary>
/// Löst einen mDNS-Hostnamen (<c>uuid.local</c>, RFC 6762/8828) zu genau einer IP-Adresse auf.
/// Für die Auflösung EMPFANGENER mDNS-ICE-Candidates (Resolution-only; der SDK publiziert selbst keine).
/// </summary>
public interface IMdnsResolver
{
    /// <summary>Die eine aufgelöste IP, oder <see langword="null"/> (nicht auflösbar/Timeout/RFC-Regel verletzt).</summary>
    Task<IPAddress?> ResolveAsync(string hostname, CancellationToken cancellationToken);
}
```

`SystemMdnsResolver.cs`:
```csharp
using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Network;

/// <summary>
/// Default-<see cref="IMdnsResolver"/>: nutzt den OS-Hostname-Resolver (<see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>),
/// der auf Systemen mit mDNS-Unterstützung (Linux+avahi/nss-mdns, macOS mDNSResponder, Windows 10+) auch
/// <c>.local</c>-Namen auflöst — RFC 8828 §3.2.2 erlaubt genau das für Resolution-only. Wendet die
/// RFC-Pflichtregeln an: Name muss <c>uuid.local</c> sein (genau ein Punkt); Auflösung zu mehr als einer
/// IP wird verworfen (Anti-Spoofing).
/// </summary>
public sealed class SystemMdnsResolver : IMdnsResolver
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookup;
    private readonly TimeSpan _timeout;

    public SystemMdnsResolver() : this(Dns.GetHostAddressesAsync, TimeSpan.FromSeconds(3)) { }

    /// <summary>Test-Ctor: injiziert den Lookup (und Default-Timeout), um die Regeln ohne echte Query zu prüfen.</summary>
    public SystemMdnsResolver(Func<string, CancellationToken, Task<IPAddress[]>> lookup)
        : this(lookup, TimeSpan.FromSeconds(3)) { }

    public SystemMdnsResolver(Func<string, CancellationToken, Task<IPAddress[]>> lookup, TimeSpan timeout)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _timeout = timeout;
    }

    public async Task<IPAddress?> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        // RFC 8828 §3.2.2: nur "<name>.local" mit GENAU einem Punkt.
        if (string.IsNullOrEmpty(hostname)
            || !hostname.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || hostname.Count(ch => ch == '.') != 1)
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var addrs = await _lookup(hostname, cts.Token).ConfigureAwait(false);
            // RFC 8828: mehr als eine IP → ignorieren.
            return addrs is { Length: 1 } ? addrs[0] : null;
        }
        catch
        {
            return null; // Timeout / SocketException / kein OS-mDNS → verworfen (verhaltensbewahrend).
        }
    }
}
```

- [ ] **Step 4: Test grün verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.Core.IntegrationTests -c Release -f net10.0 --filter "FullyQualifiedName~SystemMdnsResolverTests" --nologo`
Expected: 4/4 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Core/Infrastructure/Common/Network/IMdnsResolver.cs src/Core/Infrastructure/Common/Network/SystemMdnsResolver.cs tests/CalloraVoipSdk.Core.IntegrationTests/SystemMdnsResolverTests.cs
git commit -m "feat(webrtc): IMdnsResolver-Seam + SystemMdnsResolver (System.Net.Dns, RFC-8828-Regeln)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `WebRtcPeerConnection` — `.local`-Candidates auflösen statt droppen

**Files:**
- Modify: `src/Core/Infrastructure/WebRtc/WebRtcPeerConnection.cs`
- Test: `tests/CalloraVoipSdk.Core.IntegrationTests/WebRtcMdnsCandidateTests.cs`

**Kontext:** Der Kern-Fix. ctor bekommt einen optionalen `IMdnsResolver` (Default `SystemMdnsResolver`). `AddIceCandidateAsync` startet bei `.local` eine Hintergrund-Auflösung (peer-lifecycle-gebunden via neuem `_mdnsLifetime`) und speist den aufgelösten Endpoint in die bestehende buffer/AddRemoteCandidate-Logik. `DisposeAsync` cancelt `_mdnsLifetime`.

- [ ] **Step 1: Failing Test schreiben (gemockter Resolver → .local wird aufgelöst + eingespeist)**

Da `AddRemoteCandidate` an einer echten Session hängt, prüft der Test das über das öffentliche Verhalten: mit einem Resolver, der eine bekannte IP liefert, darf `AddIceCandidateAsync` für einen `.local`-Candidate NICHT mehr still verwerfen. Wir prüfen, dass der Resolver aufgerufen wird (Observability über einen Zähl-Resolver) und keine Exception fliegt. Der vollständige End-to-End-Weg (bis AddRemoteCandidate) ist in Task 5 (echter Browser) abgedeckt.

```csharp
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class WebRtcMdnsCandidateTests
{
    private sealed class CountingResolver : IMdnsResolver
    {
        public int Calls;
        public string? LastHost;
        private readonly IPAddress? _result;
        public CountingResolver(IPAddress? result) => _result = result;
        public Task<IPAddress?> ResolveAsync(string hostname, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastHost = hostname;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task LocalCandidate_Triggers_Mdns_Resolution()
    {
        var resolver = new CountingResolver(IPAddress.Parse("192.168.7.7"));
        await using var peer = WebRtcTestPeerFactory.Create(mdnsResolver: resolver);
        _ = peer.CreateOffer(); // bindet den Media-Socket, damit ein Candidate verarbeitbar ist

        await peer.AddIceCandidateAsync("candidate:1 1 udp 2113937151 abcd1234.local 54321 typ host");
        // Die Auflösung läuft im Hintergrund; kurz pollen bis der Resolver gerufen wurde.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (resolver.Calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);

        Assert.Equal(1, resolver.Calls);
        Assert.Equal("abcd1234.local", resolver.LastHost);
    }

    [Fact]
    public async Task Ip_Candidate_Does_Not_Trigger_Mdns_Resolution()
    {
        var resolver = new CountingResolver(null);
        await using var peer = WebRtcTestPeerFactory.Create(mdnsResolver: resolver);
        _ = peer.CreateOffer();

        await peer.AddIceCandidateAsync("candidate:1 1 udp 2113937151 192.168.1.20 54321 typ host");
        await Task.Delay(200);

        Assert.Equal(0, resolver.Calls); // reiner IP-Pfad unverändert
    }
}
```

**Hinweis:** `WebRtcTestPeerFactory.Create(...)` ist der bestehende Test-Helfer, mit dem `WebRtcPeerToPeerTests` einen `WebRtcPeerConnection` baut. Falls es keinen zentralen Helfer gibt, den lokalen Bau-Code aus `WebRtcPeerToPeerTests.BuildPeer` wiederverwenden und um den `mdnsResolver`-Parameter erweitern. **Vor der Umsetzung `grep -rn "new WebRtcPeerConnection(" tests/` prüfen** und den vorhandenen Konstruktions-Pfad nutzen.

- [ ] **Step 2: Test rot verifizieren**

Run: `dotnet test tests/CalloraVoipSdk.Core.IntegrationTests -c Release -f net10.0 --filter "FullyQualifiedName~WebRtcMdnsCandidateTests" --nologo`
Expected: FAIL — der ctor kennt `mdnsResolver` noch nicht / `.local` wird verworfen (Calls == 0).

- [ ] **Step 3: `WebRtcPeerConnection` anpassen**

(a) Feld + ctor-Parameter (nach `turnProbe`):
```csharp
    private readonly IMdnsResolver _mdnsResolver;
    private readonly CancellationTokenSource _mdnsLifetime = new();
```
ctor-Signatur erweitern (letzter optionaler Parameter):
```csharp
        TurnAllocationProbe? turnProbe = null,
        IMdnsResolver? mdnsResolver = null)
```
im ctor-Body (bei den Zuweisungen):
```csharp
        _turnProbe = turnProbe;
        _mdnsResolver = mdnsResolver ?? new SystemMdnsResolver();
```
`using CalloraVoipSdk.Core.Infrastructure.Common.Network;` oben ergänzen.

(b) `AddIceCandidateAsync` (Z. 495) — den `.local`-Zweig einbauen. Die bestehende Einspeisung (buffer/AddRemoteCandidate) in eine private Hilfe `EnqueueRemoteCandidate(IPEndPoint, long)` extrahieren (DRY), dann:
```csharp
    public Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (ParseCandidateFields(candidate) is not { } parsed)
        {
            _logger.LogDebug("Ignoring an unusable trickled ICE candidate.");
            return Task.CompletedTask;
        }

        if (IPAddress.TryParse(parsed.Address, out var ip))
        {
            EnqueueRemoteCandidate(new IPEndPoint(ip, parsed.Port), parsed.Priority);
            return Task.CompletedTask;
        }

        if (parsed.Address.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            // mDNS-Auflösung im Hintergrund (RFC 8838: Candidates kommen asynchron dazu; der Signaling-
            // Pfad wird nicht blockiert). An die Peer-Lebensdauer gebunden.
            var host = parsed.Address;
            var port = parsed.Port;
            var priority = parsed.Priority;
            _ = ResolveAndEnqueueAsync(host, port, priority);
            return Task.CompletedTask;
        }

        _logger.LogDebug("Ignoring an unusable trickled ICE candidate.");
        return Task.CompletedTask;
    }

    private async Task ResolveAndEnqueueAsync(string host, int port, long priority)
    {
        try
        {
            var ip = await _mdnsResolver.ResolveAsync(host, _mdnsLifetime.Token).ConfigureAwait(false);
            if (ip is null)
            {
                _logger.LogDebug("mDNS (.local) ICE candidate could not be resolved; relying on the peer's other candidates.");
                return;
            }
            EnqueueRemoteCandidate(new IPEndPoint(ip, port), priority);
        }
        catch (OperationCanceledException) { /* Peer disposed */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "mDNS (.local) ICE candidate resolution failed.");
        }
    }

    private void EnqueueRemoteCandidate(IPEndPoint endpoint, long priority)
    {
        BundledMediaSession? session;
        lock (_sync)
        {
            session = _session;
            if (session is null)
            {
                _pendingRemoteCandidates.Add((endpoint, priority));
                return;
            }
        }
        session.AddRemoteCandidate(endpoint, priority);
    }
```
`ParseCandidateFields` ersetzt `ParseTrickleCandidate`, gibt aber das validierte `SdpIceCandidate` zurück (Adresse noch NICHT zu IP geparst), damit `.local` erkennbar bleibt:
```csharp
    private static SdpIceCandidate? ParseCandidateFields(string candidate)
    {
        var value = candidate.Trim();
        if (value.StartsWith("a=", StringComparison.Ordinal)) value = value[2..];
        if (value.StartsWith("candidate:", StringComparison.Ordinal)) value = value["candidate:".Length..];

        if (SdpIceCandidate.TryParse(value) is not { } parsed
            || parsed.Component != 1
            || !parsed.Transport.Equals("udp", StringComparison.OrdinalIgnoreCase)
            || parsed.Port <= 0
            || parsed.Priority < 0)
            return null;
        return parsed;
    }
```
(Die alte `ParseTrickleCandidate`-Methode entfernen; ihre einzige Aufrufstelle war `AddIceCandidateAsync`.)

(c) `DisposeAsync` — `_mdnsLifetime` canceln + entsorgen (an der bestehenden Dispose-Stelle, früh, damit laufende Auflösungen abbrechen):
```csharp
        _mdnsLifetime.Cancel();
        // ... bestehender Dispose-Ablauf ...
        _mdnsLifetime.Dispose();
```
(In den bestehenden `DisposeAsync`-Body einfügen; `Cancel()` an den Anfang, `Dispose()` ans Ende, passend zur vorhandenen Struktur.)

- [ ] **Step 4: Test grün + volle WebRtc-Suite regressionsfrei**

Run: `dotnet test tests/CalloraVoipSdk.Core.IntegrationTests -c Release -f net10.0 --filter "FullyQualifiedName~WebRtcMdnsCandidateTests" --nologo`
Expected: 2/2 PASS.
Run: `dotnet test tests/CalloraVoipSdk.Core.IntegrationTests -c Release -f net10.0 --filter "FullyQualifiedName~WebRtc" --nologo`
Expected: alle grün (kein Regress im ICE/Candidate-Pfad).

- [ ] **Step 5: Commit**

```bash
git add src/Core/Infrastructure/WebRtc/WebRtcPeerConnection.cs tests/CalloraVoipSdk.Core.IntegrationTests/WebRtcMdnsCandidateTests.cs
git commit -m "feat(webrtc): .local-mDNS-ICE-Candidates auflösen statt droppen (WebRtcPeerConnection)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Verdrahtung — Default-Resolver + Options-Überschreibbarkeit

**Files:**
- Modify: `src/Core/Infrastructure/WebRtc/WebRtcSessionFactory.cs`
- Modify: ggf. `src/Client/WebRtc/WebRtcConfiguration.cs` / `WebRtcOptions.cs` (nur falls der Seam öffentlich konfigurierbar sein soll)

**Kontext:** Der ctor-Default (`SystemMdnsResolver`) greift schon ohne Verdrahtung (Paket funktioniert out-of-the-box). Diese Task stellt sicher, dass die Produktions-Konstruktion den Default nutzt und — SIPSorcery-Muster — ein eigener Resolver optional injizierbar ist. **YAGNI-Check:** Wenn kein Kundenbedarf für einen Custom-Resolver besteht, NUR verifizieren, dass der Default greift, und die öffentliche Options-Erweiterung weglassen.

- [ ] **Step 1: Konstruktionspfad prüfen**

Run: `grep -rn "new WebRtcPeerConnection(" src/`
Expected: die Produktions-Aufrufstelle(n) (in `WebRtcSessionFactory`/`WebRtcClient`). Sicherstellen, dass sie ohne expliziten `mdnsResolver` bauen → der ctor-Default `SystemMdnsResolver` greift. Kein Code nötig, wenn der Default reicht.

- [ ] **Step 2: (nur bei Bedarf) Options-Seam**

Falls ein öffentlicher Custom-Resolver gewünscht ist: `WebRtcPeerOptions` um `IMdnsResolver? MdnsResolver { get; init; }` erweitern und in `WebRtcSessionFactory` an den ctor durchreichen. **Sonst diese Task ohne Code-Änderung abschließen** (Default genügt). Entscheidung im Commit dokumentieren.

- [ ] **Step 3: Build**

Run: `dotnet build src/Core/CalloraVoipSdk.Core.csproj -c Release -f net10.0 -p:CodeAnalysisTreatWarningsAsErrors=true --nologo`
Expected: 0 Warnungen / 0 Fehler.

- [ ] **Step 4: Commit** (nur falls Step 2 Code brachte; sonst überspringen)

```bash
git add src/Core/Infrastructure/WebRtc/WebRtcSessionFactory.cs
git commit -m "feat(webrtc): mDNS-Resolver in der Peer-Konstruktion verdrahtet (Default SystemMdnsResolver)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: E2E — Browser mit Default-mDNS interoperiert

**Files:**
- Modify: `tests/CalloraVoipSdk.BrowserInteropTests/WebRtcBrowserInteropTests.cs`

**Kontext:** Der GA-Beweis: derselbe Browser-Interop-Test wie Paket 1, aber mit **mDNS AKTIVIERT** im Browser (Default-Browser-Verhalten). Vor Paket 2 würde er scheitern (der SDK droppte `.local`); jetzt löst der SDK auf → connect + bidir Audio klappt.

- [ ] **Step 1: Zweiten Test ergänzen (mDNS an)**

Ans Ende von `WebRtcBrowserInteropTests.cs` eine Variante, die den Browser mit Default-mDNS startet. Am einfachsten die bestehende Testmethode um einen `bool disableMdns`-Parameter erweitern und als `[Theory]` mit beiden Werten fahren — ODER (klarer) eine zweite `[BrowserRequiredFact]`, die `browser.NavigateAsync(bridge.BaseUri, disableMdns: false)` ruft und dieselben Assertions macht. Konkret die zweite Fact:
```csharp
    [BrowserRequiredFact]
    public async Task SdkOfferer_Connects_With_Default_Mdns_Candidates()
    {
        // Wie SdkOfferer_Connects..., aber der Browser läuft mit AKTIVEM mDNS (Default-Browser):
        // er schickt .local-Candidates, die der SDK jetzt auflöst (Paket 2). Vor Paket 2: Timeout.
        await RunBrowserInteropAsync(disableMdns: false);
    }
```
Dazu die bestehende Testmethode `SdkOfferer_Connects_And_Exchanges_Audio_With_RealBrowser` in einen privaten Helfer `RunBrowserInteropAsync(bool disableMdns)` extrahieren (den `browser.NavigateAsync(bridge.BaseUri)`-Aufruf zu `browser.NavigateAsync(bridge.BaseUri, disableMdns)` machen) und die bestehende Fact ruft `RunBrowserInteropAsync(disableMdns: true)`.

- [ ] **Step 2: Beide Browser-Interop-Tests grün (measure-first)**

Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --filter "FullyQualifiedName~SdkOfferer_Connects" --nologo`
Expected: **2/2 grün** — sowohl mit als auch OHNE mDNS-Disable. Der mDNS-an-Fall ist der neue GA-Beweis.
**Falls der mDNS-an-Test FAIL (measure-first):** Browser-Logs im Assert-Dump prüfen. Wahrscheinliche Ursachen: (a) die Auflösung dauert länger als die Connect-Deadline → die Deadline in `RunBrowserInteropAsync` moderat erhöhen (mDNS-Query-Latenz); (b) der `.local`-Candidate kommt, aber die Auflösung liefert eine IP, die auf loopback nicht erreichbar ist (Chromium meldet die LAN-IP, der SDK bindet Loopback) → `LocalEndPoint` auf die primäre LAN-IP setzen (wie in der Paket-1-measure-first-Notiz). NICHT die Assertion schwächen.

- [ ] **Step 3: 2× wiederholen (Stabilität)** und Commit

```bash
git add tests/CalloraVoipSdk.BrowserInteropTests/WebRtcBrowserInteropTests.cs
git commit -m "test(webrtc): E2E — Browser mit Default-mDNS interoperiert (SDK löst .local auf)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Voll-Regression + Spike-Aufräumen

**Files:**
- Modify/Delete: `tests/CalloraVoipSdk.BrowserInteropTests/MdnsResolutionSpikeTests.cs` (Spike aus Task 1)

- [ ] **Step 1: Spike-Test entfernen** (die Machbarkeit ist jetzt durch Task 5 dauerhaft abgedeckt)

```bash
git rm tests/CalloraVoipSdk.BrowserInteropTests/MdnsResolutionSpikeTests.cs
```

- [ ] **Step 2: Volle Regression**

Run: `dotnet build CalloraVoipSdk.sln -c Release --nologo 2>&1 | tail -3` → 0 Fehler.
Run: `dotnet test tests/CalloraVoipSdk.Core.IntegrationTests -c Release -f net10.0 --filter "FullyQualifiedName~WebRtc|FullyQualifiedName~SystemMdnsResolver" --nologo` → alle grün.
Run: `dotnet test tests/CalloraVoipSdk.BrowserInteropTests -c Release -f net10.0 --nologo` → alle grün (Playwright-Spike + Bridge-Unit + beide Browser-Interop-Tests).

- [ ] **Step 3: `git diff` gegen `src/` sichten**

Run: `git diff --stat feat/webrtc-browser-interop..HEAD -- src/`
Expected: nur die 2 neuen Resolver-Dateien + `WebRtcPeerConnection.cs` (+ ggf. `WebRtcSessionFactory.cs`). Chirurgischer Fix, kein Kollateral.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test(webrtc): mDNS-Spike entfernt (durch E2E dauerhaft abgedeckt) + Voll-Regression grün

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Abschluss

Nach Task 6: der SDK löst empfangene `.local`-mDNS-Candidates auf; die Fassade interoperiert mit **Default-Browsern** (mDNS an) — der letzte fundamentale Browser-Interop-Blocker der GA-Reifung ist geschlossen. Branch `feat/webrtc-mdns-resolution` per `superpowers:finishing-a-development-branch` abschließen (PR durch den User; gestapelt auf Paket 1). **Verbleibende GA-Reifung (Pakete 3+):** Video (VP8) Browser-Interop · Browser-Offerer-Richtung · Answerer-Relay-Gap · TCP/TLS-TURN-Config-Diagnostik · Video-Stats.
