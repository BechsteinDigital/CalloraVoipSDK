# ADR-053: QoS as an Observed Metric, Not a Marked Packet

Status: Accepted
Date: 2026-07-08

## Context

VoIP call quality on a shared network is governed by two levers: **marking** outbound media so
routers can prioritise it (Differentiated Services / DSCP, e.g. EF for RTP per RFC 4594), and
**observing** the delivered quality (RFC 3550 interarrival jitter, RTCP-derived round-trip time,
loss). The C15 cluster raised QoS as a product concern. The concrete work that landed under it was
not packet marking at all — it was making the *observation* plane correct after a real Fritz!Box
acceptance call surfaced two frozen metrics: `jitterMs` stuck at exactly the 20 ms frame interval,
and `rttMs` stuck at a never-updated 60 ms default.

This ADR records the deliberate stance the cluster settled: the SDK **measures** QoS and feeds the
adaptive media path from those measurements; it does **not** DSCP/ToS-mark media sockets.

### Verified current state

- **No DSCP / ToS / traffic-class marking exists anywhere in production code.** A tree-wide search
  for `dscp | typeofservice | SetSocketOption | IPTOS | traffic-class | qos` over `*.cs` in `src/`
  returns zero production hits — the only `qos` match is the test name
  `tests/.../QosMetricsTests.cs`. Every media UDP socket is created plain: `RtpSession`
  (`Infrastructure/Rtp/Session/RtpSession.cs:166`), `BundledMediaTransport`
  (`Infrastructure/Rtp/BundledMediaTransport.cs:101`), the SIP audio/video sockets in
  `Infrastructure/Sip/Adapters/SipCoreCallChannel.cs:143/148`, and the WebRTC socket in
  `Infrastructure/WebRtc/WebRtcPeerConnection.cs:780`. The media socket sets only
  `ReceiveBufferSize` and `Bind` (`RtpSession.cs:167-170`) — no `SetSocketOption` for
  `IP_TOS`/`SO_PRIORITY`, no `DontFragment`.
- **No QoS config surface exists.** `src/Core/Sdk/` has no DSCP/priority/QoS option; nothing is
  configurable because nothing is marked.
- **The jitter estimator is RFC 3550 §6.4.1** and now measures variance, not the frame interval.
  `BundledSourceReceptionState.UpdateJitter` (`Infrastructure/Rtp/BundledSourceReceptionState.cs:201`)
  computes `transitDifference = |arrivalDeltaRtpUnits − rtpDelta|` and smooths
  `J += (transitDifference − J)/16`. The original defect was a `double → uint` saturation of the
  arrival time (~1.4e13 ≫ `uint.MaxValue`) that pinned every arrival to the same value, collapsing
  `J` onto exactly one frame duration; the fix was integer arithmetic with an explicit
  modulo-2³² `unchecked` truncation. Exposed via `IJitterBuffer.EstimatedJitterMs`
  (`Infrastructure/Rtp/JitterBuffer/IJitterBuffer.cs:18`) → `CallMediaRuntimeMetrics.EstimatedJitterMs`
  (`Application/Media/CallMediaRuntimeMetrics.cs:76`).
- **RTCP-derived RTT is now actually delivered to the media path.** `CallRtcpQualityMonitor`
  computes RTT from LSR/DLSR and pushes it via
  `ICallMediaSession.UpdateRoundTripTimeHint` (`Application/Media/ICallMediaSession.cs:28`,
  fed at `CallRtcpQualityMonitor.cs:555`) into `RtpCallMediaSession.UpdateRoundTripTimeHint`
  (`Infrastructure/Rtp/RtpCallMediaSession.cs:333`) and the bundled equivalent
  (`BundledCallMediaSession.cs:71`). Before the fix the value was computed but stranded in the
  monitor, so the buffer kept its `InitialRoundTripTimeMs` default forever
  (comment preserved at `CallRtcpQualityMonitor.cs:552`).
- Correctness is pinned by `tests/.../QosMetricsTests.cs`, whose own doc-comment names the
  regression ("jitterMs froze at exactly the frame interval (20.00 ms) … rttMs reported the
  never-updated 60 ms default").

## Decision

Treat QoS as an **observed and consumed** signal, not a marked one:

1. **Measure** interarrival jitter (RFC 3550 §6.4.1) and RTCP-derived RTT correctly, and surface
   them on the runtime metrics (`EstimatedJitterMs`, the RTT hint feed).
2. **Feed the adaptive media path** from those measurements — the RTT hint drives the jitter
   buffer's delay floor (RTT consumption mechanics are owned by C09-02), so the observation plane
   is not merely a dashboard but an input to playout.
3. **Do not DSCP/ToS-mark media sockets.** No `IP_TOS`/`SO_PRIORITY` socket option is set on any
   media socket, and no QoS config surface is offered.

### Crux

The cluster name ("QoS", with an implied "mark RTP as EF") and what shipped diverge on purpose.
DSCP marking is cheap to *set* but **not portable and rarely honoured**: on Windows the OS strips
application-set DSCP unless a Group-Policy QoS policy grants it (raw `IP_TOS` is ignored for
non-admin processes), and end-to-end it survives only inside a single administratively-controlled
DiffServ domain — across the public Internet or a consumer CPE (the Fritz!Box in the field report)
the codepoint is almost always re-bleached to best-effort. A marking the SDK cannot guarantee is
worse than none: it invites a false "QoS is handled" claim. The measurable, always-available lever
is the observation plane feeding the adaptive buffer, so that is what was built and hardened.

## Consequences

Positive: jitter now reports true variance and RTT tracks the real RTCP measurement, both feeding
the adaptive delay floor — the metrics are trustworthy inputs, not decoration. No unportable,
silently-dropped socket option sits in the media path pretending to prioritise traffic.

Honest divergence / limits:

- **This is a negative decision on the cluster's headline feature.** There is *no* EF/DSCP marking
  for RTP. A deployment that owns its DiffServ domain and wants router-level prioritisation gets
  nothing from the SDK today and would need the follow-up below. The cluster label "QoS" overstates
  what is implemented if read as "traffic prioritisation".
- Jitter resolution is bounded at ~1 ms by the `ToUnixTimeMilliseconds` arrival quantisation
  (≈ 8 RTP units at 8 kHz) — sufficient for telephony monitoring; sub-ms would need a
  Stopwatch-based receive clock (this is partially superseded by the C21 monotonic-clock work).
- RTT only converges once the peer references our SR via LSR; the value stays at the
  `InitialRoundTripTimeMs` seed until the first qualifying inbound report (seed mechanics: C09-02).
- The monitor's raw RTT snapshot and the buffer's smoothed RTT deliberately differ slightly for the
  same source.

## Guardrails

- No media socket may set `IP_TOS`/`SO_PRIORITY`/DSCP without an accompanying decision, because a
  silently-dropped marking is a false capability claim; if marking is added it must be explicitly
  documented as best-effort and per-deployment opt-in, never a default that implies guarantees.
- The jitter estimator must keep measuring variance, not the frame interval: the integer /
  `unchecked` truncation in `UpdateJitter` must not regress to floating-point arrival conversion
  (`QosMetricsTests` "perfectly-timed arrivals → jitter < 2 ms" is the guard).
- A computed QoS metric must reach its consumer, not strand in the monitor: RTCP-derived RTT stays
  wired through `UpdateRoundTripTimeHint` to the media session
  (`QosMetricsTests` "valid LSR/DLSR → session hint ≈ measured RTT").

## Sources

- `docs/archive/agent-log/2026-07-08-dev-qos.md` — QoS-metrics run (jitter overflow root cause + RTT
  plumbing fix, tests, caveats).
- `docs/reference/decision-inventory.md` — cluster C15 "QoS/DSCP-Markierung Media".
- Code (verified): `Infrastructure/Rtp/BundledSourceReceptionState.cs:201` (jitter),
  `Infrastructure/Rtp/JitterBuffer/IJitterBuffer.cs:18`,
  `Application/Media/ICallMediaSession.cs:28` + `Application/Media/CallRtcpQualityMonitor.cs:555`
  (RTT feed), `Infrastructure/Rtp/Session/RtpSession.cs:166-170`,
  `Infrastructure/Rtp/BundledMediaTransport.cs:101`,
  `Infrastructure/Sip/Adapters/SipCoreCallChannel.cs:143/148`,
  `Infrastructure/WebRtc/WebRtcPeerConnection.cs:780` (plain sockets, no ToS),
  `tests/CalloraVoipSdk.Core.IntegrationTests/QosMetricsTests.cs`.
- Related ADRs: C09-02 (jitter-buffer RTT convergence seed — owns RTT *consumption*),
  C21-01 (monotonic media clock — the finer arrival-clock follow-up).
