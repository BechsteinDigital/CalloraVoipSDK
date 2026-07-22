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

## Pull request flow

1. Fork the repo and create a branch from `main` (e.g. `fix/srtcp-tag-length`).
2. Make your change with tests; run the build and the architecture gates locally.
3. Open a PR against `main`. Fill in the PR template, and link the issue it resolves
   (e.g. `Fixes #6`).
4. CI must be green. A maintainer will review; expect questions on RFC compliance and
   threading for stack changes.

## Commit messages

Use clear, imperative messages. Conventional-commit prefixes (`fix:`, `feat:`, `docs:`,
`test:`) are appreciated but not required.

## Licensing

By contributing, you agree that your contributions are licensed under the project's
[Apache-2.0 license](LICENSE). If you add third-party code or dependencies, update
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
