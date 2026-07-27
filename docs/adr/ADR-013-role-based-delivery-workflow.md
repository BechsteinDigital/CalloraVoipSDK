# ADR-013: Role-Based Delivery Workflow (CEO / PO / DEV / Reviewer)

Status: Accepted
Date: 2026-04-14

## Context

CalloraVoipSdk is a single-maintainer commercial SDK developed with AI agents. Without a
disciplined process, agent runs tend to conflate three failure modes: (1) drifting scope
("opportunistic side work"), (2) status inflation (declaring items `DONE`/`compliant` without
evidence), and (3) working from stale planning documents. The 2026-04-14 runs surfaced all three:
the CEO run for CORE-116 was based on an outdated `CLAUDE.md` table (it flagged CORE-006 as an open
Phase-1 gap although it had been `DONE` since 2026-04-13), and the PO run had to correct that
against the actual TODO source of truth before scoping DEV work.

### Verified current state

The role separation is a governance decision recorded in the project instructions, not a code
artifact — so the "state" here is the process contract and its enforcement points:

- **`CLAUDE.md` (Agentenprinzip)** fixes the four roles and their hand-off rule: *CEO priorisiert und
  entscheidet · PO schneidet Scope und Akzeptanz · DEV implementiert exakt das freigegebene Paket ·
  REVIEWER prueft Scope, Risiken, Tests und Claim-Wahrheit*, with "Kein automatisches Skill-Chaining
  als globale Regel" and "Rollenuebergaben nur, wenn explizit ausgeloest."
- The four 2026-04-14 role logs are the reference execution of the loop for one item (CORE-116):
  `2026-04-14-ceo.md` (Start-Freigabe, Handover an PO) → `2026-04-14-po.md` (DEV-Vorgabe mit
  nummerierten Akzeptanzkriterien + Claim-Grenzen) → `2026-04-14-dev.md` (implementation) →
  `2026-04-14-reviewer.md` (GRÜN mit Checkliste + `PENDING_USER_MERGE_APPROVAL`).
- The Reviewer gate is code-anchored: every review runs a fixed checklist (Akzeptanzkriterien, DDD,
  Thread-Safety, ≤1000 Zeilen, DI, XML-Docs, Try/Catch-Logging, Tests grün, kein Scope-Creep) that
  maps 1:1 onto the mechanically enforced `EngineeringRulesTests` gates
  (`tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs`).

## Decision

Adopt a strictly role-based, single-package-per-run delivery workflow:

1. **CEO** selects and prioritises exactly one CORE item / approved package, then hands off.
2. **PO** cuts scope: numbered acceptance criteria, explicit non-goals, and **Claim-Grenzen**
   (what may and may not be claimed after success). One approved package per run.
3. **DEV** implements exactly the approved package on a feature branch — no opportunistic side work;
   new follow-up work is *noted*, not silently built.
4. **REVIEWER** verifies scope adherence, risk, tests, and claim-truth against a fixed checklist,
   and returns `GRÜN` / `APPROVED WITH FOLLOW-UPS` — merges to `main` gate on explicit user approval
   (`PENDING_USER_MERGE_APPROVAL`).

### Crux design pieces

- **Claim-truth over test-green.** A green suite is evidence of behaviour, never an automatic
  `DONE`/`compliant`/`abgeschlossen` upgrade. Each package carries explicit `Claim-Grenzen`; docs may
  never assert more than code + tests + scope coverage prove.
- **Source-of-truth discipline.** Roles read the specialised sources (POLICY/STATE/TODO), not a
  stale `CLAUDE.md` table — the PO's first act on 2026-04-14 was correcting the CEO's stale-table
  analysis and re-prioritising against the real TODO.
- **No global skill-chaining.** Role transitions fire only when explicitly triggered; there is no
  automatic CEO→PO→DEV→Reviewer cascade.
- **Reviewer checklist ≙ mechanical gates.** The human/agent review checklist mirrors the CI
  architecture gates, so the same invariants are checked twice — once by a reasoning reviewer, once
  by the deterministic `EngineeringRulesTests`.

## Consequences

Positive: scope stays small and auditable; every claim is traceable to code + tests; stale-doc
drift is caught by the PO layer; merges are user-gated. The 2026-04-14 CORE-116 run demonstrates the
loop end-to-end including a caught stale-doc error and a `PENDING_USER_MERGE_APPROVAL` hold.

Tradeoffs: overhead per change (four hand-offs for one package); the process is only as good as the
source-of-truth hygiene it depends on. The workflow is a governance contract in `CLAUDE.md`, not
itself mechanically enforced — divergence between the documented loop and an individual run is
possible and must be caught by the Reviewer, not a gate.

**Log↔code divergence:** the four 2026-04-14 logs reference item IDs (CORE-109/112/116) and a test
baseline count ("775/775", "821/821") that reflect the repo state at that date; today's suite and
item registry differ. The *process* decision is unchanged and current; the *numbers* in those logs
are historical and were not re-verified against today's code.

## Guardrails

- Exactly one approved package per run; no opportunistic side work — follow-ups are noted, not built.
- `DONE`/`Erledigt`/`vollstaendig`/`compliant`/`abgeschlossen` only with direct code + tests + scope
  evidence; test-green alone never upgrades status.
- Documentation must never claim more than the technical proof supports.
- Role transitions only on explicit trigger; no automatic skill-chaining.
- Reviewer checklist stays aligned with the mechanical `EngineeringRulesTests` gates.

## Sources
- Logs: docs/archive/agent-log/2026-04-14-ceo.md, docs/archive/agent-log/2026-04-14-po.md,
  docs/archive/agent-log/2026-04-14-dev.md, docs/archive/agent-log/2026-04-14-reviewer.md
- Code / process anchors: CLAUDE.md (Agentenprinzip, Scope- und Claim-Regeln),
  tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs
- Marker: CORE-109 / CORE-112 / CORE-116
