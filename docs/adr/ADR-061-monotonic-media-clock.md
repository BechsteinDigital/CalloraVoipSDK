# ADR-061: Monotonic Clock for Time-Based Media and RTCP Computations

Status: Accepted
Date: 2026-07-24

## Context

Several media and RTCP computations are pure elapsed-time deltas: the jitter buffer's
interarrival-jitter estimate and playout schedule, and the RFC 3550 §6.4.1 round-trip time
(`arrival - sentAt - DLSR`) plus the DLSR (`now - remoteSrReceivedAt`) we advertise to the peer.
All of these originally read their instants from `DateTimeOffset.UtcNow` — the wall clock.

A wall clock is not monotonic. NTP steps, manual clock changes, DST transitions, and leap-second
smears can move `UtcNow` **backward or forward** mid-call. For a delta-based computation that is a
correctness bug, not a rounding nuisance:

- **Jitter buffer** — a backward step makes `now < reference` seemingly forever, which stalls
  playout; a forward step marks the whole queue "late" and dumps it.
- **RTT** — a backward step was already discarded by the existing `roundTrip > 0` guard, but a
  **forward** step between sending our SR and its echo arriving inflates the RTT to a bogus positive
  value that passes the guard.
- **DLSR** — a step in either direction skews the delay we advertise, so the peer computes a wrong
  RTT from our report.

The crux: these computations only ever consume *differences* between two instants, so they need a
jump-immune source; but the on-wire NTP timestamps and the reporting snapshots need a *real*
time-of-day and must stay on the wall clock. The two concerns share the same call sites and must be
separated cleanly.

### Verified current state (graphify + git)

- `MonotonicClock` exists at `src/Core/Infrastructure/Common/Timing/MonotonicClock.cs`
  (graphify: node `MonotonicClock`, community 582). It is an `internal static` class backed by
  `Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime(origin)`, exposing
  `DateTimeOffset Now => DateTimeOffset.UnixEpoch + Stopwatch.GetElapsedTime(OriginTimestamp)`. Its
  own XML doc states the value is a synthetic epoch-anchored instant where **only deltas are
  meaningful** (introduced in `dc01a5d`).
- `MonotonicClockTests` (`tests/CalloraVoipSdk.Core.IntegrationTests/MonotonicClockTests.cs`,
  graphify community 537) covers the two contract properties: `Now_never_decreases_across_rapid_reads`
  and `Now_advances_with_elapsed_time` — both revert-verified RED on a backward-running clock.
- **Jitter buffer path** — `RtpCallMediaSession` reads `MonotonicClock.Now` directly at both jitter
  sites: the arrival `_jitterBuffer.Add(packet, MonotonicClock.Now)` and the playout
  `_jitterBuffer.TryGetNext(MonotonicClock.Now)`. Metrics-publish instants in the same file
  (`_nextMetricsPublishAtUtc`, `CreateRuntimeMetricsSnapshot`, `PublishRuntimeMetricsIfDue`)
  deliberately stay on `DateTimeOffset.UtcNow`.
- **BUNDLE RTT path** — `BundledRtcpReporter` carries **two** injectable seams:
  `Func<DateTimeOffset> _utcNow` (default `() => DateTimeOffset.UtcNow`, stamps the on-wire NTP/RTP
  timestamps) and `Func<DateTimeOffset> _monotonicNow` (default `() => MonotonicClock.Now`, the RTT
  send instant handed to `onSenderReportSent`). One monotonic instant is shared by every SR in a
  compound. `BundledMediaSession` reads the RR arrival from `MonotonicClock.Now` for the RTT delta.
- **SIP RTCP path** — `CallRtcpQualityMonitor` lives in the **Application** layer, which DDD layering
  forbids from referencing Infrastructure (architecture-test-enforced). It therefore does **not** call
  `MonotonicClock`; instead it carries a **local duplicate** monotonic default
  (`private static readonly long MonotonicOrigin = Stopwatch.GetTimestamp();` +
  `DefaultMonotonicNow() => DateTimeOffset.UnixEpoch + Stopwatch.GetElapsedTime(MonotonicOrigin)`)
  exposed as an injectable `Func<DateTimeOffset> _monotonicNow`. Wall-clock `UtcNow` is retained for
  the NTP timestamps and the `CallQualitySnapshot` capture time; the RTT/DLSR anchor fields were
  renamed off their `*Utc` suffix.

## Decision

**Read every delta-based media/RTCP computation off a monotonic clock; keep every absolute
time-of-day (on-wire NTP timestamps, reporting snapshots) on the wall clock.**

1. Provide a `Stopwatch`-backed `MonotonicClock` under `Infrastructure/Common/Timing` — jump-free,
   epoch-anchored, only deltas meaningful.
2. Migrate the three time-critical delta sites to it:
   - jitter-buffer arrival + playout (`dc01a5d`),
   - BUNDLE RTT send instant + RR arrival (`aa517b7`),
   - SIP-path RTT + advertised DLSR (`5e38c60`).
3. Where a type may inject its time source for deterministic tests, expose the monotonic read as an
   injectable `Func<DateTimeOffset>` seam **separate from** the wall-clock seam, so a test can prove
   the delta is derived from the monotonic clock even when the wall clock is decades off.

### Crux

The single instant a delta is computed from must come from a jump-immune source; the single instant
that goes on the wire (or into a human-facing snapshot) must come from a real time-of-day. These two
instants co-locate at the same call site — the design separates them into two named reads
(`monotonicNow` vs `utcNow`) rather than one shared `now`, so neither concern silently borrows the
other's clock.

## Consequences

Positive: RTT, DLSR, and the jitter buffer survive an NTP step / manual clock change / DST / leap
smear mid-call. The forward-step RTT inflation that slipped past the `roundTrip > 0` guard is closed.
On-wire NTP timestamps and reporting snapshots remain real time-of-day, so peers and dashboards read
correct absolute times. The injectable seams make the monotonicity structurally testable
(revert-verified RED tests exist for the jitter contract, the BUNDLE send instant, and the SIP RTT).

Divergence, stated honestly:

- **Two implementations of the same clock.** Infrastructure has `MonotonicClock` (used directly by
  `RtpCallMediaSession` / `BundledMediaSession`, and as the default behind `BundledRtcpReporter`'s
  `monotonicNow` seam). The Application-layer `CallRtcpQualityMonitor` cannot reference it (DDD
  layering, enforced by architecture tests) and so carries a **local copy** of the same
  `Stopwatch`-based logic. The two are semantically identical but physically duplicated; they can
  drift if one is changed without the other. No shared Application-visible abstraction bridges them
  today.
- **Seam shape is not uniform.** `RtpCallMediaSession` and `BundledMediaSession` read
  `MonotonicClock.Now` inline (not injected — the jitter contract is proven at the `JitterBuffer`
  level and the BUNDLE RR-arrival monotonicity is structural). `BundledRtcpReporter` and
  `CallRtcpQualityMonitor` expose a `Func<DateTimeOffset>` seam. There is no single injected clock
  interface across the stacks.
- **Deliberate wall-clock retention.** NTP/RTP on-wire timestamps, `CapturedAtUtc`/snapshot instants,
  the empty-snapshot time, and metrics-publish scheduling stay on `UtcNow` **by design** — they are
  absolute reporting instants, not deltas. This is correct, not an oversight, but it means both clocks
  coexist in the same files.
- The DLSR-we-send monotonicity in the SIP path is **structural** (send-loop driven) and has no
  isolated unit test — verified by construction, not by a red/green assertion.

## Guardrails

- A monotonic instant is a synthetic epoch-anchored value: **never** interpret it as a date/time, and
  never put it on the wire. On-wire NTP timestamps and human-facing snapshots stay on the wall clock.
- Any new delta-based media/RTCP computation (jitter, RTT, DLSR, playout, interarrival) reads a
  monotonic source, never `UtcNow`.
- At a call site that both computes a delta and stamps a wire/snapshot instant, keep the two reads
  named and separate (`monotonicNow` vs `utcNow`); do not collapse them into one `now`.
- If `MonotonicClock`'s logic changes, update the Application-layer `CallRtcpQualityMonitor` copy in
  lockstep until a layering-safe shared abstraction removes the duplication.
- Monotonicity that is only structural (e.g. the SIP DLSR-we-send) must at minimum be revert-verified
  or covered by the delta-consumer's test; do not claim a monotonic guarantee without a check.

## Sources

Commits (PR #14, SIP-14 media hardening):

- `dc01a5d` — fix(rtp): drive the jitter buffer off a monotonic clock, not wall-clock. Adds
  `MonotonicClock` + `MonotonicClockTests`; migrates the arrival `Add` and playout `TryGetNext`.
- `aa517b7` — fix(rtp): compute the BUNDLE RTT on a monotonic clock, not wall-clock. Adds the
  `monotonicNow` seam to `BundledRtcpReporter`; `BundledMediaSession` reads RR arrival from
  `MonotonicClock.Now`; RTT anchor fields renamed off `*Utc`.
- `5e38c60` — fix(rtcp): compute the SIP-path RTT and DLSR on a monotonic clock. Application-layer
  local `Stopwatch` monotonic default + injectable `monotonicNow` seam in `CallRtcpQualityMonitor`;
  NTP/snapshot instants stay wall-clock. Completes the wall-clock→monotonic item across all three
  media stacks.
- `26816e5` — refactor(rtcp): decode each inbound RTCP compound once, then fan out. *Context only —
  same PR #14, not part of the clock decision.*

Code (graphify-verified):

- `src/Core/Infrastructure/Common/Timing/MonotonicClock.cs` (graphify node `MonotonicClock`, community 582)
- `tests/CalloraVoipSdk.Core.IntegrationTests/MonotonicClockTests.cs` (graphify community 537)
- `src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs` — jitter arrival/playout `MonotonicClock.Now`
- `src/Core/Infrastructure/Rtp/BundledRtcpReporter.cs` — `_utcNow` / `_monotonicNow` seams
- `src/Core/Infrastructure/Rtp/BundledMediaSession.cs` — RR arrival `MonotonicClock.Now`
- `src/Core/Application/Media/CallRtcpQualityMonitor.cs` — Application-layer local monotonic duplicate
