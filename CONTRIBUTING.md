# Contributing to CalloraVoipSdk

Thanks for your interest in contributing. This is a commercial-grade .NET VoIP SDK with a
hand-written SIP/RTP/STUN/TURN stack, so contributions are held to a high bar — but the
project is well documented to make that achievable.

## Before you start

- **Security issue?** Do **not** open a public issue — see [`SECURITY.md`](SECURITY.md).
- **Found a bug or want a feature?** Search [existing issues](../../issues) first, then
  open a new one using the templates.
- **Planning a non-trivial change?** Open an issue to discuss it before writing code —
  especially for anything touching the protocol stack.

## Getting oriented

New contributors and maintainers should read, in order:

1. [`MAINTAINING.md`](MAINTAINING.md) — architecture map, invariants, workflows.
2. [`ENGINEERING_RULES.md`](ENGINEERING_RULES.md) — the rules your PR must satisfy
   (several are enforced mechanically by the architecture tests).
3. [`docs/maintainers/`](docs/maintainers/) — flow walkthroughs, threading map, and an
   onboarding/debugging guide (including how to run the app against the Asterisk container
   and how to use the test harness).

## Development setup

Requires the .NET SDK pinned in [`global.json`](global.json) (10.0.100). Then:

```bash
# Build exactly as CI does (warnings are errors)
dotnet build CalloraVoipSdk.sln -c Release -p:CodeAnalysisTreatWarningsAsErrors=true

# Architecture gates (run these first — CI does too)
dotnet test tests/CalloraVoipSdk.ArchitectureTests -c Release

# The standard test set (matches CI: excludes long soaks and Docker interop)
dotnet test CalloraVoipSdk.sln -c Release \
  --filter "FullyQualifiedName!~CalloraVoipSdk.Core.Tests&Category!=SoakLong&Category!=Interop"
```

Full command reference (soaks, perf gate, interop) is in `MAINTAINING.md` §3.

## The bar for a pull request

Your change is expected to satisfy the rules in `ENGINEERING_RULES.md`. The ones that trip
people up most often:

- **Every change ships with a test on the lowest sensible level** (L0 wire → L1 security →
  L2 media → L3 signaling → L4 facade). Never fix a protocol bug without a test on the
  level the bug lives on.
- **Architecture-test baselines may only shrink.** If you fix a listed exception (silent
  catch, oversized file, sync-over-async), remove its baseline entry in the same PR.
- **Protocol behaviour cites its RFC and paragraph** in a comment; deliberate deviations
  are marked and justified.
- **Fail-closed for media security** — never send or accept plaintext when SRTP/DTLS is
  negotiated or required.
- **No `TODO`/`FIXME`.** Use structured follow-up comments and the marker system
  (see `docs/audit/CODE_FINDINGS_REGISTER.md`).
- **Match the surrounding code** in style, comment density, and naming.

## How work is tracked

Everything lives in GitHub Issues — there is no second tracker, so an issue, its commits,
its PR and the CI result are always one click apart.

**One finding, one issue, one PR.** A review that turns up five defects becomes five
issues plus a parent that links them as sub-issues, not one issue with five headings. The
parent carries a checklist so you can see at a glance what is left:

```markdown
### Findings
- [x] P1-1 Response authentication — #174
- [ ] P2 Fail-closed parsing — #181
```

**The issue owns the acceptance criteria.** They are checkboxes, and each one is meant to
be checkable by reading a diff. The issue closes when they are all ticked — not when a PR
merges. A merge is evidence, not proof; if a criterion is still open after the merge, the
issue stays open and says why.

Nobody ticks them automatically. A bot cannot tell whether "excess candidates create no
persistent entries" is genuinely true, so a human does it during review. That is the
whole reason the boxes exist.

### Picking something up

| Label | Meaning |
|---|---|
| [`good first issue`](../../labels/good%20first%20issue) | Small, self-contained, no deep protocol context needed |
| [`help wanted`](../../labels/help%20wanted) | Well-scoped and genuinely unclaimed |
| [`review-finding`](../../labels/review-finding) | A concrete defect with acceptance criteria — the best kind of ticket to fix |
| `P1` / `P2` / `P3` | Interop/stability critical · correctness · hygiene |

Comment on the issue before you start on anything larger than a one-liner, so two people
do not fix the same thing. No formal assignment process — saying "taking this" is enough.

## Pull request flow

1. Fork the repo and create a branch from `main` (e.g. `fix/srtcp-tag-length`).
2. Make your change with tests; run the build and the architecture gates locally.
3. Open a PR against `main` and fill in the template.
   - **Reference the issue — this is required and CI checks it.** Use `Closes #123`
     (or `Fixes` / `Resolves`). A chore with genuinely no issue behind it can carry the
     `no-issue` label instead.
   - **Copy the issue's acceptance criteria into the PR** and tick what your change
     satisfies, so a reviewer can compare the two without opening both tabs.
   - Closing only part of a parent review? Say so: `Closes #172 (P1-1 of #163).
     Remaining in #163: P1-3, P2.`
4. A bot posts your PR on the referenced issues, and posts the outcome when it closes —
   so the issue thread stays readable on its own.
5. CI must be green. A maintainer will review; expect questions on RFC compliance and
   threading for stack changes.

If you are fixing something you found yourself, open the issue first anyway. It takes a
minute, it gives the fix a place to state what "done" means, and it means the next person
who hits the same bug finds it.

## Commit messages

Use clear, imperative messages. Conventional-commit prefixes (`fix:`, `feat:`, `docs:`,
`test:`) are appreciated but not required.

**Do name the finding you are fixing** when a review issue has several:
`fix(stun): make the long-term nonce manager stateless (#156 P1-2)`. GitHub cannot query
issue bodies, so this line is what lets anyone map a commit back to the finding it closes —
including six months from now, when the file has moved and the line numbers are gone.

## Licensing

By contributing, you agree that your contributions are licensed under the project's
[Apache-2.0 license](LICENSE). If you add third-party code or dependencies, update
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
