# ADR-037: Jitter-Buffer RTT Convergence Seed

Status: Accepted
Date: 2026-07-09

## Context

The adaptive jitter buffer folds a fraction of the estimated round-trip time into its delay floor
(`ComputeAdaptiveDelayFloorMs`: `RTT × RoundTripTimeDelayWeight`), so that the playout floor
already accounts for network round-trip before loss-recovery (NACK/RTX) can matter. But RTT is a
*derived* signal — the first real sample only arrives with the first inbound RTCP report, roughly
5 s into a call. With a zero RTT seed, the floor contributed nothing for those opening seconds,
leaving an underrun risk at call start.

A naive fix (just default the seed above zero) would have broken convergence: the EWMA smoother
used `RTT <= 0` as an implicit "no sample yet" proxy, so any non-zero seed would make the *first*
real sample blend with the seed instead of locking to it — slowing convergence to the true RTT.

### Verified current state

- **`JitterBufferOptions.InitialRoundTripTimeMs`** (`Infrastructure/Rtp/JitterBuffer/
  JitterBufferOptions.cs`) defaults to **100** ms; `RoundTripTimeDelayWeight` defaults to 0.10 —
  so the seed contributes ~10 ms of floor budget from call start. Both are overridable via options.
- **`JitterBuffer`** (`Infrastructure/Rtp/JitterBuffer/JitterBuffer.cs`) initializes
  `_estimatedRoundTripTimeMs = ClampRoundTrip(_options.InitialRoundTripTimeMs)` in its ctor.
- **`JitterBuffer.UpdateRoundTripTime`** tracks an explicit `_hasRealRoundTripSample` flag under
  `lock (_sync)`: the *first* real RTCP-derived sample **replaces** the seed outright (fast lock);
  only *later* samples are EWMA-smoothed (`_estimatedRoundTripTimeMs += (clampedRtt − est) ×
  smoothing`). NaN/Infinity samples are rejected. This replaces the fragile `<= 0` proxy.
- The estimate feeds `ComputeAdaptiveDelayFloorMs` (`_estimatedRoundTripTimeMs × max(0,
  RoundTripTimeDelayWeight)`) — the seed's only effect is on the delay floor, not on loss stats.

## Decision

Seed the jitter buffer's RTT estimate with a **conservative 100 ms WAN default**, and make the
**first real RTCP RTT sample hard-replace the seed** (rather than blend), via an explicit
"have a real sample yet?" flag:

1. Default `InitialRoundTripTimeMs = 100` — a conservative WAN round-trip that yields a non-zero
   delay floor from the first packet, overridable per deployment.
2. Track `_hasRealRoundTripSample` explicitly instead of overloading `RTT <= 0` as the sentinel;
   first sample locks, subsequent samples EWMA-smooth.

### Crux

The seed and the convergence signal must not be the same variable's magnitude. Once the floor
needs a non-zero starting RTT, "is this the startup seed or a measured value?" can no longer be
inferred from the value itself — it needs its own bit of state. Getting this wrong doesn't fail
loudly; it silently slows RTT convergence at every call start, which is exactly the underrun
window the seed was meant to close. The flag makes seed-vs-measured an explicit, tested
distinction.

## Consequences

Positive: a non-zero adaptive delay floor from call start (closing the opening-seconds underrun
window) with convergence-to-truth preserved — the first measured RTT still locks immediately.

Honest divergence / limitation:
- **100 ms is a chosen default, not field-derived.** It is a conservative WAN guess; deployments
  on lower-latency paths over-budget the floor slightly until the first real sample arrives.
  Overridable via `JitterBufferOptions`.
- This is an **audio jitter-buffer** tuning decision; it does not touch the video reorder/playout
  path (C12) or the RTCP RTT/DLSR measurement itself (that lives in `CallRtcpQualityMonitor`, on a
  monotonic clock — `git 5e38c60`). The seed only affects the *floor* until real RTT arrives.

## Guardrails

- The first real RTT sample must lock (no blend with the seed); only later samples EWMA-smooth —
  regression-guarded by `JitterBufferRttSeedTests` (default → 100; first sample 40 → locks to 40;
  second 60 → EWMA 44; explicit override honored).
- Seed and "have-real-sample" must stay separate state; do not reintroduce a value-based sentinel.
- Flag reads/writes stay inside `lock (_sync)` (K3 — jitter-buffer state is lock-guarded).
- No RTT contribution to loss/discard statistics — the seed is confined to the delay floor.

## Sources

- Logs: `docs/archive/agent-log/2026-07-09-dev-b9-rtt-seed.md`
- Code (graphify-verified): `Rtp/JitterBuffer/JitterBuffer.cs`
  (`.UpdateRoundTripTime()`, `.ComputeAdaptiveDelayFloorMs()`, `_hasRealRoundTripSample`,
  `EstimatedRoundTripTimeMs`), `Rtp/JitterBuffer/JitterBufferOptions.cs`
  (`InitialRoundTripTimeMs`, `RoundTripTimeDelayWeight`); tests `JitterBufferRttSeedTests`
- Git (adjacent RTT context): `5e38c60` (compute the SIP-path RTT and DLSR on a monotonic clock,
  #14)
- Markers/RFC: RFC 3550 §6.4.1 (RTT/DLSR); ENGINEERING_RULES K3 (threading — lock-guarded jitter
  state), K7 (documented deliberate default)
