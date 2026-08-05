<!--
Thanks for contributing! Please fill this in. For security fixes, coordinate privately
first — see SECURITY.md. Contribution rules: CONTRIBUTING.md and ENGINEERING_RULES.md.
-->

## What & why

<!-- What does this PR change and why? One or two sentences. -->

**Closes:** #<!-- issue number — REQUIRED. Use one of: Closes / Fixes / Resolves #123.
     Every PR must reference the issue it addresses; CI checks for it. If no issue exists
     yet, open one first — it is where the acceptance criteria live. Chore-only PRs
     (typos, formatting, dependency bumps) may use the `no-issue` label instead. -->

<!-- If this closes only SOME of the parent review's findings, say which and link the parent:
     "Closes #172 (P1-1 of #163). Remaining in #163: P1-3, P2." -->

## Acceptance criteria

<!-- Copy the checkboxes from the issue and tick the ones this PR satisfies. A reviewer
     should be able to diff this list against the issue without opening both tabs. -->

- [ ]

## How

<!-- Brief description of the approach. For protocol changes, cite the relevant RFC/section. -->

## Checklist

- [ ] Builds clean with warnings-as-errors:
      `dotnet build CalloraVoipSdk.sln -c Release -p:CodeAnalysisTreatWarningsAsErrors=true`
- [ ] Architecture gates pass: `dotnet test tests/CalloraVoipSdk.ArchitectureTests -c Release`
- [ ] Standard test set passes (see CONTRIBUTING.md)
- [ ] **New/changed behaviour is covered by a test on the lowest sensible level**
      (L0 wire → L1 security → L2 media → L3 signaling → L4 facade)
- [ ] If an architecture-test baseline entry was resolved, it is **removed** from the baseline
- [ ] Protocol behaviour cites its RFC/paragraph; deliberate deviations are marked and justified
- [ ] Media-security paths remain **fail-closed** (no plaintext when SRTP/DTLS is required)
- [ ] Docs updated if behaviour/public API changed (`MAINTAINING.md`, `docs/`, `CHANGELOG.md`)
- [ ] No `TODO`/`FIXME`; new dependencies (if any) added to `THIRD-PARTY-NOTICES.md`

## Notes for reviewers

<!-- Anything reviewers should focus on: threading, RFC edge cases, interop risk, etc. -->
