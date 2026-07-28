# CORE-011 — Chaos/Fault-Injection-Gate — Design (2026-07-28)

**Branch:** `feat/webrtc-ice-restart` (GA-Sammelbranch, wird gesammelt gemergt) · **Ziel:** der letzte offene GA-Kern-P0. Ein CI-verdrahtetes Gate, das beweist, dass das SDK unter realen Netz-Fehlern *graceful* degradiert und sich erholt — ohne Crash, ohne Leak, mit korrektem Terminal-State.

**Scope bestätigt (User 2026-07-28):** alle 4 Fault-Klassen; eigener PR-CI-Job (härtestes GA-Gate, blockiert Merge bei Regression).

## Measure-first-Befund (Spike)

Der `InteropHarness` hat **Soak-Ausdauer + Metric-Sampling** (`ResourceSampler`, `TrendAssertions.NoUpwardSlope`-Leak-Erkennung via Regressionssteigung, `RtpMediaLoopback` = zwei echte `RtpCallMediaSession` über UDP-Loopback, `SoakArtifactSink`), aber **keine Fault-Injection-Primitive** (der ADR-058-Claim war aspirational).

Der Media-Socket ist SDK-intern (`RtpCallMediaSession` bindet `CallMediaParameters.LocalEndPoint`, kein exponierter Transport-Seam). **Der saubere, src/-freie Fault-Injection-Punkt ist ein MITM-UDP-Relay im Harness** zwischen den beiden Legs: beide Legs senden an das Relay (`RemoteEndPoint=Relay-Port`), das Relay lernt beide Quell-Adressen aus den ersten Datagrammen und leitet A↔B weiter — und entscheidet **pro Paket** über *forward / drop / delay / corrupt / inject*. Kein src/-Eingriff.

## Architektur

- **`FaultInjectingUdpRelay`** (`InteropHarness/Chaos`): bindet einen Loopback-Port, lernt die zwei Peer-Adressen, leitet symmetrisch weiter. Laufzeit-umschaltbare Fault-Policy (thread-safe): `DropRate`, `CorruptRate`, `DelayMs`, plus `InjectAsync(bytes, toLeg)` (adversariale Pakete) und `HardFault()`/`Heal()` (Total-Loss an/aus für mid-call-Transport-Loss). Zählt geleitete/gedroppte/korrumpierte Pakete für Assertions.
- **`ChaosRtpMediaLoopback`** (`InteropHarness/Chaos`): wie `RtpMediaLoopback`, aber beide Legs zeigen mit `RemoteEndPoint` auf das Relay. Bietet dieselbe Sende-/Empfangs-API plus Zugriff auf das Relay zur Fault-Steuerung.
- **Chaos-Suite** (`tests/CalloraVoipSdk.SoakTests/Chaos/*`, `[Trait("Category","Chaos")]`): treibt Last mit aktiven Faults und assertet Terminal-State + Erholung + Kein-Leak (`ResourceSampler`/`TrendAssertions`), Artefakte via `SoakArtifactSink`.
- **CI:** eigener `ci.yml`-Job (wie der Browser-Gate) fährt `Category=Chaos` bei jedem PR; der Haupt-Test-Job schließt `Category=Chaos` aus (kein Doppellauf). PR-Profil = deterministisch + gebundene Iterationen (schnell); Langprofil optional nightly.

## Fault-Klassen (Slices)

1. **Transport-Loss mid-call** (Media): Relay `HardFault()` mitten in einem laufenden Call → empfangende Seite bekommt keine Frames mehr, **kein Crash/keine entkommende Exception**, dann `Heal()` → Frames fließen wieder (Erholung). Kein Leak.
2. **Malformed-Pakete** (Wire-Robustheit): Relay injiziert Garbage/truncated Datagramme + korrumpiert einen Anteil der RTP/SRTP-Pakete → Depacketiser/SRTP-Auth verwerfen robust (SRTP fail-closed), Call bleibt stabil, kein Crash.
3. **Signaling-Timeout/Retransmit** (SIP): ein Fault-SIP-Responder (auf Basis `SipRegisterLoopHarness`) lässt Transaktionen ins Leere laufen → RFC-3261-Timer, sauberes Scheitern, **kein wedged State**, kein Leak über wiederholte Zyklen.
4. **Resource-Churn unter Fault** (Leak): schnelles Connect/Disconnect **mit** aktiven Relay-Faults → `TrendAssertions.NoUpwardSlope` auf Managed/Private-Memory + Handles/Sockets: kein Leak unter Fehlerbedingungen. Danach der CI-Job.

## Kadenz

Pro Slice: Injektor/Test → grün → Commit. Review + eigener CI-Job + PR am Paketende. Die erste Slice liefert `FaultInjectingUdpRelay` + `ChaosRtpMediaLoopback` (die Fault-Klassen 2 & 4 bauen darauf auf).

## Nicht in Scope

Der „wired-up performance gate" (README) — separat. Native Fault-Injection *im* SDK-Transport (bräuchte src/-Seam) — das MITM-Relay deckt die realen Wire-Fehler ohne src/-Eingriff.
