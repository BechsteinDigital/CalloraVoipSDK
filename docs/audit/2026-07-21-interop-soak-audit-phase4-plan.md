# Phase 4.1 — Asterisk-Interop REGISTER-Durchstich (L4): Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** Der erste echte Fremd-Stack-Interop-Durchstich — das SDK registriert per SIP/UDP an einen **Asterisk in einem Docker-Container** (Testcontainers). Nur REGISTER (kein Call/Media — RTP-Networking kommt in 4.2). Lokal (Docker läuft) + CI-fähig; env-gated (Skip ohne Docker).

**Architecture:** Neues Test-Projekt `CalloraVoipSdk.InteropTests` (net8/9/10, Testcontainers 4.13.0, referenziert Client+Core+InteropHarness). Eine `AsteriskContainer`-Fixture startet `andrius/asterisk:22` mit einer minimalen `pjsip.conf`. Der Interop-Test nutzt die öffentliche Facade `VoipClient.ConnectAsync` gegen den gemappten Host-Port.

**Tech Stack:** .NET 8/9/10, xUnit, Testcontainers 4.13.0, Docker, Asterisk PJSIP.

---

## Verifizierte Fakten (Recon)

- **SDK-Registrierung** (public Facade): `new VoipClient(new VoipConfiguration { UserAgent = ... })`; `SipAccount { SipServer (required, IP/FQDN=Domain), Port (0→5060), Transport (default Udp), Username (required), Password (bei 401 nötig) }` (`src/Core/Domain/Lines/SipAccount.cs`); `await client.ConnectAsync(account, new ConnectOptions { Timeout })` (`IVoipClient.cs:64`) → `ConnectResult { IsSuccess (==Registered), Status, FinalLineState, Line, Error }`.
- **IP:Port ohne DNS**: `SipDnsRouteResolver` nutzt ip-literal-Pfad bei parsebarer `SipServer`-IP → `SipServer="127.0.0.1"` + `Port=<mapped>` funktioniert (dynamischer Testcontainers-Port ok).
- **Testcontainers UDP** (KRITISCH): String-Overloads mit `/udp`-Suffix: `.WithExposedPort("5060/udp").WithPortBinding("5060/udp", assignRandomHostPort: true)`. Die **int-Overloads binden TCP** (falsch für SIP). Mapped-Port: `container.GetMappedPublicPort(5060)`.
- **Config**: `.WithResourceMapping(bytes, "/etc/asterisk/pjsip.conf")`. **Wait**: `Wait.ForUnixContainer().UntilMessageIsLogged("Asterisk Ready")` (kein UDP-Port-Wait).
- **Rest-Risiko**: UDP-Response-Adressierung über Docker-NAT — das SDK muss rport nutzen, damit Asterisk die 401/200 an die gemappte Quelle zurücksendet. Falls die Registrierung timeoutet: Asterisk-Container-Logs prüfen (kommt REGISTER an? wird Response gesendet?).

---

## File Structure

```text
tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj   NEU
tests/CalloraVoipSdk.InteropTests/DockerRequiredFactAttribute.cs       NEU  Skip wenn kein Docker
tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainer.cs        NEU  Testcontainers-Fixture
tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainerSmokeTests.cs NEU  Container-ready-Smoke
tests/CalloraVoipSdk.InteropTests/Registration/AsteriskRegisterInteropTests.cs NEU  SDK registriert
CalloraVoipSdk.sln                                                     +InteropTests
```

---

## Task 1: InteropTests-Projekt + Testcontainers + Docker-Gate

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj`
- Create: `tests/CalloraVoipSdk.InteropTests/DockerRequiredFactAttribute.cs`
- Modify: `CalloraVoipSdk.sln` (via `dotnet sln add`)

- [ ] **Step 1: csproj** — `tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.6.0" />
    <PackageReference Include="xunit" Version="2.4.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.4.5" />
    <PackageReference Include="Testcontainers" Version="4.13.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Client\CalloraVoipSdk.Client.csproj" />
    <ProjectReference Include="..\..\src\Core\CalloraVoipSdk.Core.csproj" />
  </ItemGroup>
</Project>
```
(Interop tests exercise the PUBLIC facade — only Client+Core refs, no InteropHarness needed for REGISTER.)

- [ ] **Step 2: Docker-Gate** — `tests/CalloraVoipSdk.InteropTests/DockerRequiredFactAttribute.cs` (skips when no reachable Docker daemon, so the suite stays green on Docker-less machines):

```csharp
using DotNet.Testcontainers.Builders;
using Xunit;

namespace CalloraVoipSdk.InteropTests;

/// <summary>Ein <see cref="FactAttribute"/>, das den Test überspringt, wenn kein Docker-Daemon erreichbar ist.</summary>
public sealed class DockerRequiredFactAttribute : FactAttribute
{
    private static readonly bool DockerAvailable = ProbeDocker();

    /// <summary>Erstellt das Attribut und setzt <see cref="FactAttribute.Skip"/>, falls Docker fehlt.</summary>
    public DockerRequiredFactAttribute()
    {
        if (!DockerAvailable)
            Skip = "Kein erreichbarer Docker-Daemon — Interop-Test übersprungen.";
    }

    private static bool ProbeDocker()
    {
        try
        {
            _ = new DockerEndpointAuthenticationProvider().GetAuthConfig();
            return TestcontainersSettings.OperatingSystem.DockerEndpointAuthConfig is not null;
        }
        catch
        {
            return false;
        }
    }
}
```
Note: if this probe API differs in Testcontainers 4.13.0, use the simplest reliable equivalent (e.g. attempt a lightweight client ping); the goal is only "skip cleanly when Docker is absent". Adjust as needed to compile.

- [ ] **Step 3: Add to solution:**
```bash
dotnet sln /home/dbechstein/Projekte/voip/.claude/worktrees/feat+interop-soak-audit/CalloraVoipSdk.sln add /home/dbechstein/Projekte/voip/.claude/worktrees/feat+interop-soak-audit/tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj
```

- [ ] **Step 4: Restore + build** (pulls Testcontainers):
Run: `dotnet build /home/dbechstein/Projekte/voip/.claude/worktrees/feat+interop-soak-audit/tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj -f net10.0`
Expected: 0/0 (if the Docker-probe API doesn't compile, fix it minimally first).

- [ ] **Step 5: Commit:**
```bash
git add tests/CalloraVoipSdk.InteropTests/ CalloraVoipSdk.sln
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 4.1a — InteropTests-Projekt + Testcontainers + Docker-Gate

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 2: Asterisk-Container-Fixture + Ready-Smoke (ITERATIV erlaubt)

Diese Task ist experimentell: die Asterisk-Config kann Feinschliff brauchen. Du DARFST iterieren — Container-Logs prüfen (`docker logs`), Config/Wait-String anpassen, bis der Container zuverlässig ready wird. Erfolgskriterium: der Smoke-Test startet den Container und die Wait-Strategy triggert grün.

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainer.cs`
- Create: `tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainerSmokeTests.cs`

- [ ] **Step 1: `AsteriskContainer`** — `tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainer.cs`:

```csharp
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CalloraVoipSdk.InteropTests.Asterisk;

/// <summary>
/// Startet einen Asterisk-Container (PJSIP) mit einer minimalen REGISTER-Konfiguration und
/// exponiert den gemappten SIP/UDP-Port. Nur für Interop-Tests.
/// </summary>
public sealed class AsteriskContainer : IAsyncDisposable
{
    private const int SipPort = 5060;

    // Minimale PJSIP-Config: ein UDP-Transport + ein userpass-Endpoint "6001" der REGISTER akzeptiert.
    private const string PjsipConf = """
        [transport-udp]
        type=transport
        protocol=udp
        bind=0.0.0.0:5060

        [6001]
        type=endpoint
        context=default
        disallow=all
        allow=ulaw
        auth=6001
        aors=6001

        [6001]
        type=auth
        auth_type=userpass
        username=6001
        password=secret

        [6001]
        type=aor
        max_contacts=1
        """;

    private readonly IContainer _container;

    /// <summary>Erstellt (noch nicht gestartet) den Asterisk-Container.</summary>
    public AsteriskContainer()
    {
        _container = new ContainerBuilder()
            .WithImage("andrius/asterisk:22")
            .WithResourceMapping(Encoding.UTF8.GetBytes(PjsipConf), "/etc/asterisk/pjsip.conf")
            .WithExposedPort("5060/udp")
            .WithPortBinding("5060/udp", assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Asterisk Ready"))
            .Build();
    }

    /// <summary>SIP-Account-Zugangsdaten für den konfigurierten Endpoint.</summary>
    public string Username => "6001";

    /// <summary>Passwort des konfigurierten Endpoints (Digest-Auth).</summary>
    public string Password => "secret";

    /// <summary>Docker-Host (meist 127.0.0.1/localhost).</summary>
    public string Host => _container.Hostname;

    /// <summary>Auf den Host gemappter SIP/UDP-Port.</summary>
    public ushort SipUdpPort => _container.GetMappedPublicPort(SipPort);

    /// <summary>Startet den Container und wartet, bis Asterisk SIP-ready ist.</summary>
    public Task StartAsync() => _container.StartAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
```

- [ ] **Step 2: Smoke test** — `tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainerSmokeTests.cs`:

```csharp
namespace CalloraVoipSdk.InteropTests.Asterisk;

public sealed class AsteriskContainerSmokeTests
{
    [DockerRequiredFact]
    public async Task Asterisk_StartsAndBecomesReady_AndExposesMappedUdpPort()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        Assert.True(asterisk.SipUdpPort > 0, "Kein gemappter SIP/UDP-Port.");
    }
}
```

- [ ] **Step 3: Run** — `dotnet test /home/dbechstein/Projekte/voip/.claude/worktrees/feat+interop-soak-audit/tests/CalloraVoipSdk.InteropTests/CalloraVoipSdk.InteropTests.csproj -f net10.0 --filter AsteriskContainerSmokeTests`. First run pulls the image (~slow, once). **If the wait times out**, inspect logs: run the container manually (`docker run --rm andrius/asterisk:22`) and grep the real "ready" log line; adjust `UntilMessageIsLogged(...)` to the actual string. **If pjsip fails to load**, the image's default `modules.conf`/`asterisk.conf` may need a companion mapping — add a minimal `modules.conf` (`preload => res_pjsip.so` etc.) via `WithResourceMapping`. Iterate until green; document any config you had to add in the commit message.

- [ ] **Step 4: Commit (once smoke is green):**
```bash
git add tests/CalloraVoipSdk.InteropTests/Asterisk/
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 4.1b — Asterisk-Testcontainer-Fixture + Ready-Smoke (PJSIP REGISTER-Config)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Task 3: REGISTER-Interop-Test (SDK ↔ Asterisk)

**Files:**
- Create: `tests/CalloraVoipSdk.InteropTests/Registration/AsteriskRegisterInteropTests.cs`

- [ ] **Step 1: Test:**

```csharp
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.InteropTests.Asterisk;

namespace CalloraVoipSdk.InteropTests.Registration;

public sealed class AsteriskRegisterInteropTests
{
    [DockerRequiredFact]
    public async Task Sdk_RegistersSuccessfully_AgainstRealAsterisk()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0" });
        var account = new SipAccount
        {
            SipServer = asterisk.Host,
            Port = asterisk.SipUdpPort,
            Username = asterisk.Username,
            Password = asterisk.Password,
            Transport = SipTransport.Udp,
        };

        var result = await client.ConnectAsync(
            account, new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(result.IsSuccess,
            $"Registrierung fehlgeschlagen: Status={result.Status}, LineState={result.FinalLineState}, Error={result.Error}");
    }
}
```
(Verify the exact namespaces of `VoipConfiguration`, `SipTransport`, `ConnectOptions`, `ConnectResult` against `examples/CalloraVoipSdk.Sample.BasicCalling/Program.cs` and fix usings so it compiles.)

- [ ] **Step 2: Run** — `... --filter AsteriskRegisterInteropTests`. Expected: `result.IsSuccess == true`.
  - **If it fails/times out (real interop finding OR NAT-response issue):** first debug — capture Asterisk logs (add `.WithOutputConsumer(...)` or `docker logs`), check whether the REGISTER arrived and whether Asterisk sent 401/200. If it's a genuine SDK↔Asterisk wire incompatibility (e.g. the SDK doesn't handle Asterisk's challenge format), that is a **real interop finding** — do NOT weaken; report DONE_WITH_CONCERNS with the SIP trace and we record it in `docs/audit/INTEROP_SOAK_AUDIT.md` as `Interop-Abweichung`. If it's a test-setup issue (NAT/rport/config), fix the setup and note it.

- [ ] **Step 3: Commit (only if green):**
```bash
git add tests/CalloraVoipSdk.InteropTests/Registration/
git commit -m "$(cat <<'MSG'
feat(interop-soak): Phase 4.1c — REGISTER-Interop-Durchstich SDK ↔ echter Asterisk (grün)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
MSG
)"
```

---

## Phase-4.1-Abschluss

- [ ] Voller Build → 0/0. · `dotnet test tests/CalloraVoipSdk.InteropTests/...` → grün (oder sauber geskippt ohne Docker).
- [ ] `origin/main`-Check + ggf. mergen.

**Damit:** erster echter Fremd-Stack-Interop (SDK registriert an Asterisk). Nächste Slices: **4.2** Call + Echo-Media (host-networking, RTP-Round-Trip) → dann Mid-Call/Transport/Video-Matrix + CI-Job.

## Spec-Abdeckung (Self-Review)

- Interop L4 gegen Asterisk (§4.1, §5, §6 Basis-Telefonie-REGISTER-Teil) → Task 3. · Testcontainers-Ausführung (§5) → Task 1/2. · env-gated (§5 CI/opt-in) → Docker-Gate.
- Kein Test-Fudging bei echter Interop-Abweichung → Task 3 Step 2 (→ Register). · Kein SDK-Fix.
- **Nicht in 4.1:** Call/Media (RTP-Networking, host-mode), Mid-Call, Transport/Security, Video, CI-Job-Verdrahtung, FreeSWITCH/3CX/Fritzbox.
