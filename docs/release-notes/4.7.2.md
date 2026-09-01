# CalloraVoipSdk 4.7.2

**ICE connection-setup latency patch for the 4.7 line.** 4.7.2 reworks the internal ICE connectivity-check
scheduler so a call reaches a working candidate pair faster — especially when a higher-priority candidate
(a host or server-reflexive address) is unreachable and used to stall the whole checklist behind its timeout.

The ICE latency rework is transparent — a peer that connected in 4.7.1 runs the same checks, only sooner — and
continues the 4.7.1 ICE fix ("a lower-priority reachable candidate is checked before an unreachable
higher-priority one consumes another retry round") from a single tweak into a full RFC 8445 checklist. It ships
together with a round of review-finding fixes (below); **`PublicApi.approved.txt` is unchanged** (no API break),
though a few of those fixes adjust on-wire details for correctness.

## Fixed in 4.7.2

- **Serial checklist → globally paced, overlapping checks.** Connectivity checks were run one pair at a time,
  each fully awaited before the next started, with a fixed delay between rounds. An unreachable high-priority
  pair therefore blocked every other pair behind its full timeout. Checks now start at most one per pacing
  interval (RFC 8445 §14 `Ta`) but run **concurrently** — a dead pair no longer delays the reachable ones.
- **Loss recovery moved into the STUN transaction.** A lost check used to wait out the full 2 s check timeout
  before the pair was retried. Each check now retransmits its request with the same transaction id on an
  RFC 8489 §6.1 schedule, so ordinary packet loss recovers in hundreds of milliseconds, not seconds.
- **Both ICE roles check actively.** Previously only the controlling agent probed pairs and the controlled
  agent waited passively to adopt the peer's nomination (RFC 8445 §7.2). Both roles now run ordinary checks;
  only the controlling role nominates.
- **Peer-reflexive triggered checks are prioritised and no longer gate on start-up.** A check learned from an
  inbound request (RFC 8445 §7.3.1.4) now preempts ordinary work and dispatches reactively, even before the
  local checklist's own start — closing a window where a peer-reflexive path was probed late.
- **Role-conflict handling.** An inbound role conflict (RFC 8445 §7.3.1.1) now re-computes pair priorities and
  redirects nomination to match the resolved role instead of keeping stale ordering.

## Review findings addressed

A pre-release review of the branch surfaced five issues, all fixed here:

- **ICE — superseded nomination.** A trickled candidate that outranks the pair already being nominated now
  cancels that nomination (via its generation) instead of losing the race to the lower validated pair, so the
  driver still selects the highest-priority *validated* pair (RFC 8445 §8.1.1).
- **ICE — priority-capped checklist.** At the DoS pair cap the checklist now evicts its lowest-priority
  evictable pair when a higher-priority candidate arrives, instead of dropping the newcomer — a late
  top-priority candidate is no longer excluded by earlier low-priority ones (matches SIPSorcery).
- **ICE — type-scoped candidate foundations (RFC 8445 §5.1.1.3).** Host foundations are now `h1`, `h2`, …,
  distinct from the fixed srflx (`s1`) and relay (`r1`) foundations. Exposed by the new multi-homed host
  gathering, where a second host candidate previously collided with srflx and could wrongly freeze a peer's
  NAT/relay fallback.
- **WebRTC — stable append-only track MIDs (RFC 8829).** Runtime-added tracks now always take numeric MIDs in
  call order, independent of kind. The grouped legacy layout could hand a video added before an audio the
  audio's MID, so `VideoTrack.SendFrameAsync` addressed the wrong m-line; that layout is removed. A fixed 1+1
  peer's SDP is unchanged. No reference SDK (libwebrtc, Firefox, Pion) grouped m-lines by type. See ADR-063.
- **WebRTC — recv-track DoS caps.** Recv-side simulcast RID lanes and the learned SSRC→MID/RID tables are now
  bounded (RFC 8853 / ENGINEERING_RULES §132-133), so an authenticated peer stamping a fresh RID/SSRC on every
  packet cannot exhaust process memory.

## Also fixed

- **Build under net8.0 / net9.0.** A nullable-reference warning in `SrtpHardeningTests` (`AuthKey`) was
  promoted to an error under `-warnaserror` on the older target frameworks; the test now asserts the key's
  presence explicitly. Test-only, no runtime change.

## Behaviour and limits

- **No public API change.** `PublicApi.approved.txt` is unchanged; there is nothing to migrate. A fixed 1+1
  peer's SDP is byte-identical. Peers that add runtime tracks in mixed audio/video order, or that read ICE
  candidate foundations, see the corrected (append-only MID / type-scoped foundation) wire values.
- **Nomination stays regular, not aggressive (RFC 8445 §8.1.1).** The highest-priority *validated* pair is
  nominated only once no higher-priority pair is still in flight, so an unresolved high-priority pair still
  adds its (now bounded, ~2 s worst-case) transaction budget before a lower pair is selected. This trades a
  bounded delay for always selecting the best working pair. See ADR-062.
- **Full ICE remains opt-in and not yet browser-interop-proven.** This patch improves setup latency; it does
  not change the production-readiness posture of the ICE agent. Validate ICE for your trunk before enabling it.

See [`CHANGELOG.md`](CHANGELOG.md) for the concise entry,
[`docs/adr/ADR-062-ice-checklist-pacing.md`](docs/adr/ADR-062-ice-checklist-pacing.md) for the ICE checklist
rationale, and [`docs/adr/ADR-063-jsep-append-only-track-mids.md`](docs/adr/ADR-063-jsep-append-only-track-mids.md)
for the track-MID decision.
