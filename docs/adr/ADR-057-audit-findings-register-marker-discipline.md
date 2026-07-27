# ADR-057: Audit → Findings-Register → Code-Marker as Durable, Claim-Verified Audit Memory

Status: Accepted
Date: 2026-07-22

## Context

This is a single-maintainer commercial SDK built with AI agents, where the working state of a
security/hardening audit is otherwise ephemeral: a review run produces findings, a later run
"knows" they were fixed, and the reasoning behind a subtle protocol/crypto decision is lost the
moment the transcript scrolls away. Two failure modes recur and are both documented in the C17
audit logs:

1. **Findings that outlive their transcript but not the repo.** The original finding/decision
   register lived *outside* the repo and did not survive the public-repository cut
   (`docs/audit/CODE_FINDINGS_REGISTER.md` §Zweck: "Das Original-Register dieser Marker lag
   außerhalb des Repos und ist nicht überliefert"). The ICE test suite likewise "überlebte den
   Public-Repository-Cut nicht" (CORE-006). So an out-of-repo memory is not durable memory.
2. **A single confident review can be wrong.** The 2026-07-08 full-SDK code review reported K1
   (`SrtpContext` not thread-safe) and K2 (keys never zeroed) as *Critical*; the same-day
   delta-audit direct-verified both against the real code and **overturned them** — the review
   agent had cited a path (`Srtp/SrtpContext.cs`) that does not exist (real:
   `Srtp/Context/SrtpContext.cs`) and never read the code. That correction is itself a
   claim-audit finding.

### Verified current state

- **The register exists and is git-tracked.** `docs/audit/CODE_FINDINGS_REGISTER.md` reconstructs
  every marker family referenced in code — **ADR** (architecture), **CF** (protocol correctness),
  **HARD** (security/concurrency/resource/quality), **CORE** (feature/true-up), **N** (NAT source).
  It is versioned via a targeted `.gitignore` exception (`.gitignore` L104 `!docs/audit/`, over the
  L102 `docs/*` ignore), so audit memory lives in the repo, not a transcript.
- **Findings are anchored at the code, not in a TODO.** `ENGINEERING_RULES.md` K6 ("Marker statt
  TODO") forbids `TODO`/`FIXME`/`HACK` and requires fixed findings to carry their marker
  (`CF-xxx`/`HARD-xxx`/`ADR-xxx`) directly at the code; open points are structured follow-up prose
  with a rationale. The register's "Fundstellen" column is the reverse index (`Datei:Zeile`).
- **Reconstruction is honest about its provenance.** Each register description is derived
  "ausschließlich aus dem umgebenden Code-/Kommentar-Kontext" and marked as reconstructed — it
  states the code-anchored state, not a claim to the original wording.
- **True-up is a recurring, code-verified activity.** `2026-07-07-true-up.md` re-verified every
  OPEN tracking item against code; `2026-07-08-audit.md` is a delta-audit against the prior review;
  `2026-07-16-audit.md` re-verified the CORE-P0 backlog against `main` (`7ff14ce`) and corrected a
  stale `STATE.json`. The convention "Testgrün allein ist kein Beweis für Status-Hochstufungen"
  (`CLAUDE.md`, K8 of the harte Projektregeln) is the governing claim rule.

## Decision

Make audit findings **durable, code-anchored, and claim-verified** through one loop rather than
leaving them in transcripts or an out-of-repo tracker:

1. **Every finding becomes a marker** in a stable family (ADR / CF / HARD / CORE / N). The marker is
   written at the code it governs (comment / XML-doc), and the register
   (`CODE_FINDINGS_REGISTER.md`) is its reverse index with `Datei:Zeile` provenance.
2. **The register is git-tracked**, via the explicit `!docs/audit/` exception, so audit memory
   ships with the code and survives repository cuts — the exact failure that lost the original.
3. **`TODO`/`FIXME`/`HACK` are banned** (K6); an open point is either a marker with structured
   follow-up prose or it does not exist. This is the discipline the register indexes.
4. **A finding is only "fixed" when direct-verified against code** — not when a review asserts it.
   Critical claims are cross-checked (delta-audit / a second read-only pass) before they enter
   status docs or memory; a review's severity is a hypothesis until verified.
5. **Audits run as fan-out read-only subagent passes** (six subsystem reviewers on 2026-07-08, four
   + three waves on 2026-07-16), consolidated tersely, with per-finding confidence and file:line —
   explicitly a "Zustands-Audit mit Stichproben-Tiefe", never claimed as a line-by-line full proof.

### Crux design pieces

- **In-repo over out-of-repo memory.** The register is git-tracked *because* the original was not
  and was lost. Durability is the whole point.
- **Marker-at-code as the anchor.** A finding lives at the line it governs; the register is a
  generated-style index over those markers, so the two cannot drift into two separate truths the way
  a free-standing TODO list would.
- **Verify-before-claim.** The 07-08 K1/K2 overturn is the canonical case: a high-confidence Critical
  was false because the reviewer never read the cited (nonexistent) path. Cross-verification of
  Critical claims is therefore mandatory, and the correction is logged as its own finding.
- **Reconstruction transparency.** Where the original wording is gone, the register says so and
  derives from code context only — it never fabricates a stronger provenance than it has.

## Consequences

Positive: audit state is recoverable from the repo alone; a fixed finding carries its marker at the
code and drops out of the "open" register, so debt is inventoried and traceable; a single wrong
review cannot silently promote a false Critical into memory. The register doubles as the source for
the C-cluster ADR reconstruction (this ADR among them) — the markers are how the decision inventory
was built.

Tradeoffs: the register is a **reconstruction**, so its descriptions capture the code-anchored state
and not necessarily the original finding's full intent — a caveat it states about itself. Marker and
register are kept in sync by discipline, not a gate (unlike the layering baselines of the sibling
C01-02 decision): a marker written at code without a register entry, or vice versa, is only caught by
review. The audits are sampling-depth state audits, not exhaustive proofs; coverage honesty is
recorded per run rather than enforced.

## Guardrails

- Open work is a marker with follow-up rationale, never a `TODO`/`FIXME`/`HACK` (K6).
- Findings carry their marker (`CF-`/`HARD-`/`ADR-`/`CORE-`) at the governed code; the register
  indexes them with `Datei:Zeile`.
- The register stays git-tracked under the `!docs/audit/` exception.
- A Critical/High finding is direct-verified against the real code (correct path read) before it
  enters status docs or memory; an overturned claim is logged as its own finding.
- No `DONE`/`compliant`/`abgeschlossen` from test-green alone — code + tests + scope coverage, per
  the CLAUDE.md claim rules.
- The register is honestly labelled a reconstruction where the original is not preserved.

## Sources
- Logs: docs/archive/agent-log/2026-07-07-true-up.md,
  docs/archive/agent-log/2026-07-08-full-sdk-code-review.md,
  docs/archive/agent-log/2026-07-08-audit.md, docs/archive/agent-log/2026-07-16-audit.md
- Docs: docs/audit/CODE_FINDINGS_REGISTER.md (Zweck/Herkunft, marker families), ENGINEERING_RULES.md
  (K6 Marker statt TODO), CLAUDE.md (Scope- und Claim-Regeln), .gitignore (L102 `docs/*`,
  L104 `!docs/audit/`)
- Marker families indexed: ADR, CF-xxx, HARD-xxx, CORE-xxx, N1/N2; canonical overturn = K1/K2
