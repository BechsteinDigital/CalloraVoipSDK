<!--
Thanks for contributing! Please fill this in. For security fixes, coordinate privately
first — see SECURITY.md. Contribution rules: CONTRIBUTING.md and ENGINEERING_RULES.md.
-->

## What & why

<!-- What does this PR change and why? One or two sentences. -->

Fixes #<!-- issue number, if any -->

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
