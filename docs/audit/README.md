# Audit artefacts

Two of the files here are **living registers** that the code points at by name. The rest are dated
records of work that is finished — kept because they explain why the test suite is shaped the way it
is, and read only when that question comes up.

## Living — keep these current

| File | What it is |
| --- | --- |
| [`CODE_FINDINGS_REGISTER.md`](CODE_FINDINGS_REGISTER.md) | The markers the source cites: `CF-xxx`, `HARD-xxx`. A comment saying "see HARD-C6" has nowhere to point without this file, and there are 15 of those. |
| [`INTEROP_SOAK_AUDIT.md`](INTEROP_SOAK_AUDIT.md) | The defect register from interop and soak runs (F001–F004), likewise cited from code. |

## Dated records — history, not instructions

Design documents and phase plans from the July 2026 interop/soak audit, newest first. They describe
decisions that have since been implemented; the implementation is the current truth, and where the two
disagree, [the code wins](../../ENGINEERING_RULES.md).

Nine of them are linked from `MAINTAINING.md` or from an ADR, which is the reason they are here rather
than only in the history: a reference that resolves is worth more than a tidy folder.

```
2026-07-28-*  capacity/quality evidence · chaos gate · ICE restart · RTP/RTCP port pair
2026-07-27-*  WebRTC browser interop · mDNS resolution · video interop
2026-07-25-*  PBX fixture abstraction · FreeSWITCH fixture
2026-07-23-*  two-leg media interop
2026-07-22-*  full source deep analysis (dated reference state)
2026-07-21-*  audit design (test levels L0–L4, soak methodology) and phase plans 0–6
```
