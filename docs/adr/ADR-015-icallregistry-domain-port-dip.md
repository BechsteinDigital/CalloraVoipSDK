# ADR-015: PhoneLine↔CallManager Decoupling via Domain Port `ICallRegistry` (DIP)

Status: Accepted
Date: 2026-07-08

## Context

`PhoneLine` (Domain) directly referenced the concrete `CallManager` (Application) — the last
`Domain → Application` layer violation and the final non-empty entry in `LayeringBaseline`.
`PhoneLine` needs only two operations from the call manager: register a newly created call, and
enumerate the active calls belonging to that line. Depending on the whole Application service to get
those two operations inverts the DDD dependency direction and blocks the layering gate from reaching
an empty baseline.

### Verified current state

- **`ICallRegistry` is a Domain port**: `src/Core/Domain/Calls/ICallRegistry.cs` L8, namespace
  `CalloraVoipSdk.Core.Domain.Calls`, `internal interface ICallRegistry` with `void Register(Call)`
  and `IReadOnlyCollection<ICall> Active`. All referenced types are Domain (`Call`, `ICall`). Its
  XML-doc states the intent verbatim: *"Implemented by the Application's call manager, so the Domain
  depends only on this abstraction rather than on the service itself."*
- graphify confirms the wiring: `PhoneLine --references--> ICallRegistry` (1 hop) and
  `CallManager --implements--> ICallRegistry` (1 hop). Additional implementers exist for
  test/degenerate use: `SingleCallRegistry`, `NoopCallRegistry`, `EmptyCallRegistry`.
- The decision is code-anchored in the gate comment
  (`tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs` L17–19): *"K4 vollstaendig
  behoben: … PhoneLine haengt nur noch an der Domain-Abstraktion ICallRegistry (von CallManager
  implementiert) … Kein Domain->Application/Infrastructure-Leak mehr."* — and `LayeringBaseline = []`
  (L20).
- `ENGINEERING_RULES.md` R1 elevates this to the normative pattern: *"Abhängigkeitsumkehr statt
  Ausnahme: Braucht die Domain etwas aus einer äußeren Schicht, definiert sie selbst einen Port
  (Beispiel: `ICallRegistry` in der Domain, implementiert vom Application-`CallManager` — Fix K4)."*

## Decision

Invert the `PhoneLine → CallManager` dependency with a Domain-owned port:

1. Define `ICallRegistry` (`internal`) in `Core.Domain.Calls` exposing exactly what the Domain needs
   — `Register(Call)` + `Active` (`IReadOnlyCollection<ICall>`) — using only Domain types.
2. `CallManager` (public, Application) implements `ICallRegistry`. `Active` satisfies it implicitly;
   `Register` is implemented **explicitly** (`void ICallRegistry.Register(Call) => Register(call);`)
   so the port stays `internal` on the public `CallManager` surface.
3. `PhoneLine` depends on `ICallRegistry`, dropping `using Application.Calls`. `VoipClient` still
   passes the concrete `CallManager` instance (which satisfies the port) at composition time.

### Crux design pieces

- **Narrow, Domain-typed port.** The interface exposes only the two members `PhoneLine` consumes,
  built from Domain types — the port belongs to the Domain, not to the Application implementer.
- **Explicit interface implementation to preserve API.** Implementing `Register` explicitly keeps it
  off `CallManager`'s public surface, so the fix is `internal`-only, non-breaking, no Major bump
  (contrast `DialOptions`, which required a public namespace move / Major).
- **Composition-root injection.** DI (`VoipClient`) supplies the concrete implementer; the Domain
  never names it.

## Consequences

Positive: `LayeringBaseline = []` — Domain references neither Application nor Infrastructure, gate-
enforced. The fix was behaviour- and Public-API-neutral. The pattern is now the codified template
(R1) for any future Domain→outer-layer need, and gained free testability (`Noop`/`Empty`/`Single`
registries as seams).

Tradeoffs: one more abstraction between `PhoneLine` and the live call manager; the composition root
must wire the concrete implementer. The port is `internal`, so it is an SDK-internal contract, not a
consumer extension point (intentional — consumers do not swap the call registry).

**Log↔code divergence:** none material. The log describes the exact interface members and wiring;
graphify + the source file confirm namespace, members, `PhoneLine` reference, and `CallManager`
implementation as described. The log's test counts (Core 147/147) are historical.

## Guardrails

- The Domain depends on `ICallRegistry`, never on the concrete `CallManager`.
- `ICallRegistry` stays Domain-typed (`Call`/`ICall`) and `internal`; no Application/Infrastructure
  types leak through it.
- `CallManager.Register` stays explicitly implemented so the port does not widen the public surface.
- `LayeringBaseline` stays empty; any Domain→Application `using` fails the gate.

## Sources
- Logs: docs/archive/agent-log/2026-07-08-dev-phoneline-callmanager-decouple.md
- Code: src/Core/Domain/Calls/ICallRegistry.cs (L8), CallManager (implements),
  PhoneLine (references), src/Core/Application/Calls/*,
  tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs (K4 comment L17–19,
  LayeringBaseline L20), ENGINEERING_RULES.md (R1 Abhängigkeitsumkehr)
- Marker: Fix K4, R1
