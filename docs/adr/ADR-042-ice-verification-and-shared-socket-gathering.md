# ADR-042: ICE Verification Discipline and Shared-Socket srflx Gathering

Status: Accepted
Date: 2026-07-08

## Context

Before the RFC-8445 ICE epic (C11-01/C11-02) began, a founder question about the ICE status
exposed two independent problems that this ADR records together, because they share one root: a
DONE claim that no live evidence backed.

1. **Evidence gap.** CORE-006 (ICE agent) had been tracked DONE since April, but the original
   Core.Tests suite (~831 tests) did not survive the public-repo cut, and the current tree had
   **zero** ICE tests. The DONE claim was unbacked — exactly the "Testgrün ≠ DONE / claims need
   direct evidence" rule the project enforces. A verification package rebuilt a deterministic
   agent test suite and, separately, made the NAT case runnable end-to-end.

2. **srflx gathering could not use the real media port.** The first real STUN gathering against
   `stun.l.google.com` during a founder NAT test failed with `SocketException(98)
   EADDRINUSE`, then with `SocketException(97)` address-family mismatch. Both were latent because
   gathering had never run against real STUN with an active call — the precise blind spot the
   missing tests had hidden. A server-reflexive candidate must carry the **real** media port, but
   that port is already held by the channel's reservation socket, so the `StunClient` bound a
   second socket to it (EADDRINUSE); and DNS resolved the STUN host to AAAA first, so an IPv4
   media socket sent to an IPv6 target (family mismatch).

### Verified current state (graphify + logs)

- **Agent test suite exists and drives the ports.** `CallIceAgentTests` exercises `CallIceAgent`
  deterministically through fakes of `IIceStunProbe` / `IIceTurnRelayAllocator`: gathering
  (host + srflx via STUN, +relay via TURN-Allocate, host-only fallback on STUN failure, skip when
  disabled), selection (host-host success, relay wins when direct paths fail, retry before pair
  abandonment), and failure reason codes (`ice_checks_failed` / `ice_metadata_missing` /
  `ice_candidates_missing` / `ice_no_candidate_pairs`) with the correct Failed-vs-Disabled state.
- **Shared-socket gathering.** `IStunClient.QueryBindingAsync` and `IIceStunProbe.
  TryCheckConnectivityAsync` carry an optional `Socket? sharedUdpSocket`, threaded through the
  auth retries / redirect requery down to `QueryCoreUdpAsync`
  (`src/Core/Infrastructure/Stun/Client/StunClient.cs` L62–249). When set, the client uses
  `SendToAsync`/`ReceiveFrom` on the channel's own socket **without** Connect/Dispose (ownership
  stays with the channel); the in-code comment (L222) states that binding a second socket to that
  port would fail with EADDRINUSE and that connecting the shared socket would filter later
  datagrams. So the srflx candidate carries the real media port.
- **Address-family selection.** `StunIceProbe.ResolveServerEndPointAsync` resolves the STUN host
  and calls `PickAddressForFamily(addresses, localEndPoint.AddressFamily)`
  (`.../Stun/Client/StunIceProbe.cs` L141–167), choosing an address in the **local** endpoint's
  family (A for an IPv4 media socket), and errors clearly when the host offers no matching family.

## Decision

Treat ICE status as a **claim that requires live, reproducible evidence**, and fix the two latent
gathering bugs the verification exposed:

1. **Rebuild a deterministic agent test suite** (`CallIceAgentTests`) driving `CallIceAgent`
   through fake connectivity/relay ports across gathering, selection, retry, and failure-reason
   paths — so the ICE agent's behaviour is evidenced rather than asserted by a stale tracking
   entry. Do **not** silently upgrade the CORE-006 DONE claim; the NAT acceptance ("calls work
   over NAT") is re-established only by a real founder call with STUN configured.
2. **Thread a shared UDP socket** through the whole STUN query chain so srflx gathering uses the
   channel's real media socket (`SendTo`/`ReceiveFrom`, no Connect/Dispose) instead of binding a
   second socket to the same port.
3. **Pick the STUN server address in the local socket's address family** so an IPv4 media socket
   never sends to an AAAA target.

Crux: the missing tests and the two socket bugs are the same failure — gathering had never been
exercised against real STUN with a live call, so a DONE claim survived that live behaviour would
have refuted. The fix is evidence first, then the bugs the evidence surfaces.

## Consequences

Positive: the ICE agent has a deterministic regression suite again; srflx gathering carries the
correct media port on a shared socket and degrades cleanly across address families; the NAT path
became actually runnable (agent gains `Agent__StunServer` / `Agent__TurnServer` configuration,
LAN behaviour unchanged without it). Loopback regression tests against the built `StunServer`
prove the shared-socket query returns the correct mapped endpoint and that the old
second-socket path would throw.

Honest limits / divergence:

- **CORE-006 stays formally DONE in tracking** (deliberately not edited), but this ADR records
  that the NAT acceptance was **not** live-verified at the time — it depends on a real
  external-NAT founder call (mobile data, STUN set), and a Fritz!Box LAN does not exercise real
  ICE. The verification package proved non-regression + gathering, not "NAT works".
- **This predates and motivated the RFC-8445 epic.** At verification time the agent still had no
  trickle, no roles/nomination, no prflx — those are the C11-01 work, explicitly out of scope here.
- **TURN gathering has the same second-socket pattern.** The shared-socket fix was applied to the
  STUN path; `TurnIceRelayAllocator` still uses the bind-a-socket pattern and was flagged to be
  migrated to the shared socket at first real TURN use (noted, not done here).

## Guardrails

- An ICE / CORE-006-style DONE claim MUST be backed by a live-behaviour test or a real-call
  acceptance; a stale tracking entry is not evidence (project claim rule).
- srflx gathering MUST reuse the channel's media socket via `SendTo`/`ReceiveFrom` without
  Connect/Dispose — never bind a second socket to the reserved media port.
- STUN server resolution MUST pick an address in the local endpoint's address family and fail with
  a clear message when none matches.
- Ownership of the shared socket stays with the channel; the STUN client MUST NOT dispose it.

## Sources

- Logs: docs/archive/agent-log/2026-07-08-dev-ice-verification.md (Teil 1 test rebuild;
  Nachtrag 1 EADDRINUSE/shared socket; Nachtrag 2 IPv6/IPv4 family conflict)
- Code: `StunClient.QueryBindingAsync` / `QueryCoreUdpAsync` (`sharedUdpSocket`, EADDRINUSE
  comment, src/Core/Infrastructure/Stun/Client/StunClient.cs);
  `StunIceProbe.ResolveServerEndPointAsync` / `PickAddressForFamily`
  (src/Core/Infrastructure/Stun/Client/StunIceProbe.cs);
  `IIceStunProbe.TryCheckConnectivityAsync` (`sharedUdpSocket`)
- Tests: CallIceAgentTests (gathering / selection / retry / failure-reason);
  StunClient loopback regression (shared-socket mapped endpoint + old-path throws);
  PickAddressForFamily mixed-AAAA/A regression
- Marker: RFC 8445 (ICE, srflx §5.1.1.2); CORE-006; project claim-evidence rule
  (CLAUDE.md "Scope- und Claim-Regeln"); EADDRINUSE(98) / family(97) founder NAT tests
