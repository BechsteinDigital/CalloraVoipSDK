# ADR-054: TURN Relay as a First-Class ICE Candidate

Status: Accepted
Date: 2026-07-19

## Context

BUNDLE media (ADR-010, ADR-011) runs over one shared 5-tuple driven by a send-side ICE agent
(C11: `IceMediaConsentSession`, `IceNominationDriver`, `IceMediaAttachment`). Until this cluster,
that agent modelled exactly one local send path — the media socket — so it could offer only host
and server-reflexive (srflx) candidates. srflx is not a second send path: it is the STUN-mapped
*view* of the same socket, so `_sendRaw` (`SendToAsync`) served both. When both peers sit behind
NATs that block a direct path (symmetric NAT, restrictive firewalls), there is no working pair and
the call has no media — the classic reason TURN exists (RFC 8656): a relay allocation on a public
server that forwards packets between the two peers.

The architectural question was whether a TURN relay is a *separate transport* bolted on beside the
direct one, or a **third local ICE candidate** that competes in the same connectivity checks. The
distinguishing property of a local candidate (RFC 8445 §5.1.1) is its *send path*: host/srflx frame
directly onto the socket; a relay frames each datagram through a TURN server (Send indication,
RFC 8656 §10). Making relay a candidate means one nomination machinery, one consent loop, and
"force relay" collapses to an ICE policy (relay-only) over the same code — not a parallel stack.

### Verified current state

- `IceLocalCandidate` (`src/Core/Infrastructure/Stun/Ice/IceLocalCandidate.cs`) is the abstraction
  point: a local candidate is a `Type` (host/srflx/relay), a `Priority`, and a `Check` delegate
  `(remoteTarget, useCandidate, ct) → Task<bool>`. Its doc states plainly: host and srflx share the
  direct send path, "a relay candidate carries its own path (framed through a TURN server), which is
  what makes more than one local candidate meaningful."
- `IceMediaConsentSession.SendCheckVia(send, target, useCandidate, ct)`
  (`.../Ice/IceMediaConsentSession.cs` L170) sends a check over an *injected* send path;
  `SendCheckAsync` delegates to it with the direct `_sendRaw` (byte-identical direct path). The
  nomination state (remote + sendVia) is one atomic `IceNominatedTarget` reference, so consent
  freshness follows the nominated pair's path with no torn read.
- `IceMediaAttachment` takes an optional `relaySend`; when present it builds a second
  `IceLocalCandidate` of type `relay` with RFC 8445 §5.1.2.1 priority at **type-preference 0** (below
  host/srflx, so a working direct pair is always preferred) and hands it to `IceNominationDriver`,
  which pairs local × remote and orders by pair priority. `OnDriverNominated` routes a relay
  nomination through `_relaySend` (a consent redirect); a direct nomination is unchanged. The dedup
  key is `(remote, sendVia)`, so a relay↔direct switch on the same remote is not swallowed as a no-op.
- The relay send path itself is `TurnRelayCandidateSendPath`
  (`src/Core/Infrastructure/Turn/Client/TurnRelayCandidateSendPath.cs`): its `SendAsync(datagram,
  remoteTarget, ct)` matches the candidate `Check` delegate shape exactly — it installs a per-peer-IP
  permission (RFC 8656 §9), frames the datagram as a Send indication addressed to the remote
  (RFC 8656 §10), and hands it to an injected raw-send delegate. It holds no socket and no transport
  type, so it composes above any media transport.
- The relay candidate can be adopted **after** the agent is running: `IceNominationDriver.AddLocalCandidate`
  and `IceMediaAttachment.AddRelayLocalCandidate(relaySend)` pair a late local candidate against all
  known and later-trickled remotes — required because the Answerer only binds its socket (and gathers
  the allocation) after `SetRemoteDescription`, i.e. after the session was built direct-only.

## Decision

Model the TURN relay as a **first-class local ICE candidate**, distinguished only by its send path,
not as a parallel transport:

1. **The send path is the candidate.** `IceLocalCandidate.Check` is the abstraction point. Host/srflx
   bind `Check` to the direct socket send; the relay binds it to `TurnRelayCandidateSendPath.SendAsync`,
   which frames through the TURN server. The nomination driver, consent session, and pair-priority
   ordering are shared unchanged — a relay candidate "plugs in exactly like the direct one."
2. **Direct is always preferred.** The relay candidate carries RFC 8445 type-preference 0, so it wins
   only when no direct (host/srflx) pair works. "Force relay" is then just an ICE policy that suppresses
   the direct candidates — the same machinery, no separate transport.
3. **Consent follows the nominated path.** `SendCheckVia` lets the consent loop (C11) send freshness
   checks over whichever path was nominated; the nominated target (remote + send path) is read as one
   atomic reference so relay↔direct nomination transitions never observe a torn state.
4. **Late adoption for the Answerer.** The agent accepts a relay local candidate after it is running
   (`AddLocalCandidate` / `AddRelayLocalCandidate`), because the allocation is gathered on the shared
   socket, and for the Answerer that socket is bound only after `SetRemoteDescription`. The
   Offerer wires the candidate at session-construction time; the Answerer adopts it post-construction.

### Crux

`_sendRaw` was already the single choke point for "send one datagram to a remote"; srflx reusing it
proved a candidate is *only* its send path. So the relay is not new transport machinery — it is a
second `Check` delegate whose body happens to frame through TURN. Everything above (priority ordering,
nomination, consent freshness, dedup) is untouched. The atomic `IceNominatedTarget` (remote + sendVia
in one reference) is what makes a live relay↔direct switch safe on the concurrent driver/receive threads.

## Consequences

Positive: one ICE agent, one consent loop, one nomination path serve host, srflx, and relay. The
direct path is byte-identical when no `relaySend` is injected (production defaulted to `null` through
several slices precisely to keep it verifiably behaviour-neutral). "Force relay" needs no new code path.

Divergence and honest open edges:

- **Controlled-agent relay gap.** Only the *controlling* agent drives nomination and installs relay
  permissions. `IceMediaAttachment.Nominate(IPEndPoint)` remains for the controlled agent's inbound
  subscription and does **not** build a relay candidate on that side. Consequence: Offerer-relay ↔
  Answerer-direct works, but Answerer-owning-the-relay while the Offerer is direct does not — the
  controlled side neither triggers its own relay nomination nor installs a permission. This needs a
  design and is not built.
- **No real socket relay round-trip until end-to-end validation.** Each slice was tested against a
  fake TURN server over loopback (permission install, Send-indication framing, MESSAGE-INTEGRITY from
  allocation credentials). A full relay pair being *nominated* over a real session socket is
  timeout-bound (direct exhaustion ~10 s), so the whole-session relay nomination is not exercised in a
  fast unit test — the glue pieces are tested individually.
- **No browser interop validation** (ADR-009 guardrail): no production-ready relay claim until an
  end-to-end run against a real TURN server and a browser peer exists.
- Priority tuning (foundation "3", type-pref 0, local-pref 65535) follows RFC 8445 §5.1.2 but has not
  been validated against a browser's candidate ordering in a real trickle exchange.

## Guardrails

- Do **not** turn the relay into a separate transport or a second consent loop — the candidate/send-path
  abstraction is the invariant. New candidate types extend `IceLocalCandidate.Check`, not the agent.
- The relay candidate stays at type-preference 0: a working direct pair must always be preferred.
- Read the nominated target (remote + send path) as one atomic `IceNominatedTarget` reference; never
  split it back into two volatile fields (the C11 torn-read finding).
- The relay send path holds no transport type and does its own I/O only through the injected raw-send
  delegate — keep the Rtp/ICE layers off the TURN module (module boundary).
- No `relaySend` wired in production without the corresponding permission/allocation stack (ADR-C16-02)
  and dispose ordering (ICE drained before the transport) in place.

## Sources

- Logs (`docs/archive/agent-log/`):
  - `2026-07-19-dev-relay-4d-3b-1.md` — relay-capable ICE agent: `SendCheckVia`, `Nominate(remote,
    sendVia)`, second `IceLocalCandidate` at type-pref 0, atomic `IceNominatedTarget`, `(remote, send)`
    dedup; controlled-agent inbound `Nominate(IPEndPoint)` kept.
  - `2026-07-19-dev-relay-4d-3b-2b-i.md` — `TurnRelayCandidateSendPath` outbound engine (per-IP
    permission dedup, Send-indication framing, NONCE threading).
  - `2026-07-19-dev-relay-4d-3b-2b-ii-B.md` — Offerer-path producer; the `_gatheredRelay` timing finding.
  - `2026-07-19-dev-relay-4d-3b-2c.md` — Answerer late-local-candidate adoption (`AddLocalCandidate`,
    `AddRelayLocalCandidate`, `AdoptRelay`).
  - `2026-07-19-dev.md` (slice 4d-2b) — media-socket-bound relay gathering (`TurnAllocationProbe`,
    `RelayCandidate` builder, allocation retention for post-start adoption).
- Code:
  - `src/Core/Infrastructure/Stun/Ice/IceLocalCandidate.cs`
  - `src/Core/Infrastructure/Stun/Ice/IceMediaConsentSession.cs` (`SendCheckVia` L170)
  - `src/Core/Infrastructure/Stun/Ice/IceMediaAttachment.cs`, `IceNominationDriver.cs`, `IceNominatedTarget.cs`
  - `src/Core/Infrastructure/Turn/Client/TurnRelayCandidateSendPath.cs`
- Markers / RFC: RFC 8445 §5.1.1, §5.1.2 / §5.1.2.1 (candidate types, priority); RFC 8656 §9
  (CreatePermission), §10 (Send/Data indications); RFC 8839 (relay candidate SDP). Adjacent: C11
  ICE ADRs (send-side agent, consent, shared-socket gathering), ADR-010/011 (BUNDLE transport),
  ADR-009 (browser-interop boundary).
