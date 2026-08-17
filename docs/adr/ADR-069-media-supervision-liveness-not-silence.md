# ADR-069: Media Silence Is Reported, Not Terminated On

Status: Accepted
Date: 2026-08-17

## Context

`MediaSupervisionOptions.InboundMediaTimeout` stood at 15 seconds and **terminated** the call when inbound
RTP had been silent that long. The rationale was sound — behind NAT a far-end BYE may never reach our
in-dialog Contact, the media simply stops, and an agent should not keep talking to a dead line — but the
signal it measured was the wrong one.

Silence is not evidence that the far end is gone. Three ordinary situations produce it while the peer is
perfectly reachable:

- **Silence suppression / comfort noise** (RFC 3389): a sender with VAD emits far fewer packets in speech
  pauses, or none.
- **Hold**: depending on the peer, `sendonly`/`inactive` carries no RTP at all.
- **Bridge switches**: the gap between legs during an attended transfer.

This is not theory. #256 was an attended-transfer interop test that went red intermittently: the harness
pumped RTP for 8 seconds and then went quiet while the consultation call and the transfer ran. Once the
silence passed 15 seconds, **our own supervisor** hung up the callee call. The Asterisk trace shows no BYE
towards the callee at any point — the PBX was never involved.

### What the reference stacks do

Verified against primary sources, not documentation summaries, because the memory rule for this SDK is
parity-or-better with the reference implementations.

| Stack | Trigger | Threshold | Hold | Reaction | Default |
|---|---|---|---|---|---|
| **SIPSorcery** (`SIPUserAgent`) | RTP **or RTCP** silent | 30 s (`NO_ACTIVITY_TIMEOUT_FACTOR 6 × 5000 ms`, settable) | exempt, local **and** remote | `Hangup()` | on |
| **Asterisk** `res_pjsip` | RTP silent | `rtp_timeout` | separate `rtp_timeout_hold` | channel hung up | **0 = off** |
| **FreeSWITCH** | RTP silent | `media_timeout` | separate value | hangup, cause `MEDIA_TIMEOUT` | **0 = off** |
| **pjsip** (library) | — | — | — | no built-in detection | — |
| **libwebrtc / Pion** | — | ICE consent 30 s (RFC 7675) | — | ICE state change, app decides | — |

Two corrections to the assumptions in #261 came out of this. First, **terminating is not the outlier**:
SIPSorcery's user agent — our comparable layer, not the bare `RTPSession` underneath it — calls `Hangup()`
on timeout, and Asterisk and FreeSWITCH hang up too when enabled. Second, the actual outliers were the
threshold (15 s against everyone else's 30 s or off) and **what resets the clock**: SIPSorcery counts RTP
*or RTCP*, and we counted RTP alone.

### What the measurement then showed

The obvious repair — count RTCP as liveness, on the assumption that a peer which is alive keeps reporting on
the RFC 3550 §6.2 interval — was tested against both reference PBXes before being believed, in
`TwoLegTransferMatrix`: bridge two SDK legs through the PBX, stop sending, and sample the callee's inbound
RTCP counter every two seconds through 20 s of silence.

| PBX | inbound RTCP during 20 s of media silence |
|---|---|
| Asterisk 22 (`direct_media=no`) | `rtcp_rx` 0 → **1** after ~4 s, then frozen for the rest |
| FreeSWITCH (`direct_media` off) | `rtcp_rx` 0 → **2** by ~8 s, then frozen for the rest |

**The assumption is false with a PBX in the media path.** As a relay with nothing to forward, neither
Asterisk nor FreeSWITCH keeps an RTCP beacon running; both go quiet on both planes. RTCP is a reliable
liveness signal only against an endpoint that reports unconditionally — SIPSorcery does, which is why the
rule works in *its* topology and not in ours. In the same runs our supervisor hung up a demonstrably live
call (Asterisk: callee terminated locally after 8 s; FreeSWITCH: caller first, then the PBX cleared the
callee with `NORMAL_CLEARING`).

So there is no signal on the wire that separates "the peer went quiet" from "the peer went away". That is
the fact this decision has to be built on — and it is precisely why Asterisk and FreeSWITCH ship their own
equivalents disabled.

## Decision

**Media silence is reported to the application; it does not end calls.** The teardown remains available, on
loss of liveness rather than of media, and is off by default.

1. **Report, don't terminate — the shipped default.** Media silence for `MediaSilenceNotifyAfter`
   (default 15 s) raises `ICall.MediaFlowChanged`, and again when media resumes, carrying the length of the
   silence that ended. `InboundMediaTimeout` defaults to `TimeSpan.Zero` (off). This is parity with Asterisk
   and FreeSWITCH, which disable their equivalents, and it is the only defensible default given the
   measurement: without a liveness beacon, any threshold ends live calls, and this one demonstrably did.

2. **The application decides, which the references do not offer.** SIPSorcery's app learns nothing until the
   call is already gone; Asterisk's and FreeSWITCH's operators get a hangup cause after the fact. An SDK
   consumer is told *while the call is still up* and can play a prompt, escalate, end the call on its own
   policy, or ignore it. That is the "better, not merely equal" part of this decision.

3. **When enabled, liveness is inbound RTP or inbound RTCP.** Either one proves the far end is present.
   `CallMediaRuntimeMetrics` gained `RtcpPacketsReceived` for this; `RtpCallMediaSession` counts inbound
   compounds before the fan-out, so a throwing subscriber cannot make a live peer look dead. RTCP can only
   extend the deadline, never shorten it — worth having even though the measurement shows a PBX will not
   supply it.

4. **The teardown carries a reason.** A media-timeout hangup sets a `CallTerminationReason`
   (`Failed`/`Local`, "Media timeout: no inbound RTP or RTCP from the far end"), so a consumer can tell an
   SDK-initiated teardown from a peer BYE on `CallStateChangedEventArgs.TerminationReason`. FreeSWITCH makes
   the same distinction with its `MEDIA_TIMEOUT` cause; SIPSorcery does not. This required parking the reason
   before the BYE: the channel reports `Terminated` from inside its own `HangupAsync` with only the generic
   locally-terminated reason it can derive from SIP, so a reason passed after that call reached an
   already-terminated aggregate and was dropped.

5. **A dead peer is still caught, on the signalling plane.** RFC 4028 session timers (ADR-023, default
   1800 s) end a dialog whose peer stopped refreshing. Slower than a media timeout, but it is evidence rather
   than a heuristic — and it is what pjsip relies on, having no media-timeout mechanism at all.

6. **Hold stays exempt** (`HangupHeldCallOnSilence`, default false), covering both local hold and remote hold
   — `Call.HandleRemoteHoldChanged` moves the call to `OnHold` as well, so the exemption applies to a peer
   that put *us* on hold. This matches SIPSorcery and Asterisk's separate `rtp_timeout_hold`.

7. **The policy is a testable state machine.** The decision moved out of `CallMediaOrchestrator` into
   `MediaActivity.Observe(metrics, state, options, now)` returning a `MediaSupervisionOutcome`. The clock is a
   parameter, so every threshold, exemption and once-only guarantee is pinned by a unit test instead of by a
   20-second interop run. The orchestrator only performs the effects.

## Consequences

- The class of false teardowns that produced #256 is gone by default: the SDK no longer ends a call because
  media stopped. Pinned end to end in `TwoLegTransferMatrix` against both PBXes — 20 s of silence, the call
  survives, the silence and its end are reported, and the transfer afterwards still delivers media.
- **Consumer-visible default change:** `InboundMediaTimeout` goes from 15 s to `TimeSpan.Zero` (off). A
  deployment that wants the teardown sets it explicitly — 30 s is the recommended value — and gets the
  liveness semantics, not the old media-silence one, so the same number fires less often than before.
- New public API: `ICall.MediaFlowChanged` and `CallMediaFlowChangedEventArgs`, plus
  `VoipConfiguration.MediaSilenceNotifyAfter` / `VoipOptions.MediaSilenceNotifyAfter`. Recorded in
  `PublicApi.approved.txt` (ADR-006 §4).
- `MediaSilenceNotifyAfter` must be shorter than `InboundMediaTimeout` when both are enabled; the options
  validator rejects the inverted configuration rather than accepting a warning that could never fire.
- **The NAT case the old default served is now the application's to handle.** A far-end BYE lost behind NAT
  leaves a call up until the session timer expires. `MediaFlowChanged` is the signal to act on; a deployment
  that prefers the old automatic teardown enables it with one setting.
- **Not covered:** ICE consent freshness (RFC 7675) would be a cryptographically authenticated liveness
  source, and unlike RTCP a peer must answer it. Per ADR-041 it is built but unwired on the SIP path — wiring
  it would make a media-timeout teardown evidence-based rather than heuristic, and is the natural follow-up.

## References

- RFC 3550 §6.2 (RTCP transmission interval), RFC 3389 (comfort noise), RFC 7675 (ICE consent freshness)
- `sipsorcery-org/sipsorcery` — `src/SIPSorcery/net/RTCP/RTCPSession.cs` (`NO_ACTIVITY_TIMEOUT_FACTOR`,
  `NoActivityTimeoutMilliseconds`) and `src/SIPSorcery/app/SIPUserAgents/SIPUserAgent.cs` (`OnRtpTimeout`)
- Asterisk `res_pjsip` endpoint options `rtp_timeout` / `rtp_timeout_hold`
- FreeSWITCH `media_timeout` (deprecated `rtp-timeout-sec`), hangup cause `MEDIA_TIMEOUT`
- pjproject#4030 (no built-in RTP timeout detection)
- Issues #261 (this decision) and #256 (the interop flake it explains)
