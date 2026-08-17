# ADR-069: Media Supervision Ends Calls on Loss of Liveness, Not on Silence

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
threshold (15 s against everyone else's 30 s or off) and, decisively, **what resets the clock**: SIPSorcery
counts RTP *or RTCP*, and we counted RTP alone. A peer in any of the three situations above keeps reporting
RTCP on the RFC 3550 §6.2 interval, so it was demonstrably alive the whole time we were declaring it dead.

## Decision

Supervision measures **liveness**, and silence becomes information for the application rather than a verdict.

1. **Liveness is inbound RTP or inbound RTCP.** Either one proves the far end is present and routable.
   `CallMediaRuntimeMetrics` gained `RtcpPacketsReceived` for this; `RtpCallMediaSession` counts inbound
   compounds before the fan-out, so a throwing subscriber cannot make a live peer look dead.

2. **Two stages.** Media silence for `MediaSilenceNotifyAfter` (default 15 s) raises
   `ICall.MediaFlowChanged` and does nothing else; loss of liveness for `InboundMediaTimeout` (default 30 s)
   ends the call. The event fires again when media resumes, carrying the length of the silence that ended.

3. **Beyond the references, deliberately.** SIPSorcery's application learns nothing until the call is already
   gone. Ours is told while the peer is still alive and can act on its own policy — play a prompt, escalate to
   an agent, end the call sooner than the SDK would, or ignore it. That is the "better, not merely equal" part
   of this decision; everything else here is parity.

4. **The teardown carries a reason.** A media-timeout hangup sets a `CallTerminationReason`
   (`Failed`/`Local`, "Media timeout: no inbound RTP or RTCP from the far end"), so a consumer can tell an
   SDK-initiated teardown from a peer BYE on `CallStateChangedEventArgs.TerminationReason`. FreeSWITCH makes
   the same distinction with its `MEDIA_TIMEOUT` cause; SIPSorcery does not.

5. **On by default, unlike Asterisk and FreeSWITCH.** For a PBX with an administrator watching, off is a
   defensible default. For an SDK whose calls sit behind NAT and whose consumers will not build their own
   dead-line detection, leaving zombie calls up is the worse failure. The threshold and both stages are
   configurable, including off.

6. **Hold stays exempt** (`HangupHeldCallOnSilence`, default false), covering both local hold and remote hold
   — `Call.HandleRemoteHoldChanged` moves the call to `OnHold` as well, so the exemption applies to a peer
   that put *us* on hold. This matches SIPSorcery and Asterisk's separate `rtp_timeout_hold`.

7. **The policy is a testable state machine.** The decision moved out of `CallMediaOrchestrator` into
   `MediaActivity.Observe(metrics, state, options, now)` returning a `MediaSupervisionOutcome`. The clock is a
   parameter, so every threshold, exemption and once-only guarantee is pinned by a unit test instead of by a
   20-second interop run. The orchestrator only performs the effects.

## Consequences

- The class of false teardowns that produced #256 is gone: a peer that reports RTCP is never hung up, at any
  silence length. The interop flake's product cause is removed, not just its test symptom.
- **Consumer-visible default change:** calls that would previously have been torn down after 15 s of media
  silence now survive as long as the peer keeps reporting. Deployments that relied on the old aggressive
  teardown must set `InboundMediaTimeout` explicitly — and should be aware it now means "no RTP *and* no
  RTCP", so it will fire less often than the same number did before.
- New public API: `ICall.MediaFlowChanged` and `CallMediaFlowChangedEventArgs`, plus
  `VoipConfiguration.MediaSilenceNotifyAfter` / `VoipOptions.MediaSilenceNotifyAfter`. Recorded in
  `PublicApi.approved.txt` (ADR-006 §4).
- `MediaSilenceNotifyAfter` must be shorter than `InboundMediaTimeout`; the options validator rejects the
  inverted configuration rather than accepting a warning that could never fire.
- **Not changed:** RTCP is liveness evidence, not a quality signal, here. A call where RTCP flows and media
  does not is still a broken call — it is simply the application's decision what to do about it, which is the
  point of the event.
- **Not covered:** ICE consent freshness (RFC 7675) would be a third, cryptographically authenticated
  liveness source, but per ADR-041 it is built and unwired on the SIP path, so it is not consulted here.

## References

- RFC 3550 §6.2 (RTCP transmission interval), RFC 3389 (comfort noise), RFC 7675 (ICE consent freshness)
- `sipsorcery-org/sipsorcery` — `src/SIPSorcery/net/RTCP/RTCPSession.cs` (`NO_ACTIVITY_TIMEOUT_FACTOR`,
  `NoActivityTimeoutMilliseconds`) and `src/SIPSorcery/app/SIPUserAgents/SIPUserAgent.cs` (`OnRtpTimeout`)
- Asterisk `res_pjsip` endpoint options `rtp_timeout` / `rtp_timeout_hold`
- FreeSWITCH `media_timeout` (deprecated `rtp-timeout-sec`), hangup cause `MEDIA_TIMEOUT`
- pjproject#4030 (no built-in RTP timeout detection)
- Issues #261 (this decision) and #256 (the interop flake it explains)
