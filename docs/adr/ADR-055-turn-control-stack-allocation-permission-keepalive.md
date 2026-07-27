# ADR-055: TURN Control Stack — Allocation, Permission, and Keepalive over the Shared Socket

Status: Accepted
Date: 2026-07-19

## Context

Modelling the relay as an ICE candidate (ADR-C16-01) needs a working TURN allocation underneath it:
an allocation on the server (RFC 8656 §7), a permission for each peer IP before any datagram is
relayed (§9), a bound channel or Send/Data indications to carry the payload (§10–12), and refreshes
to keep both the allocation and the permissions alive for the call's duration (§3.9, §9). All of this
must ride the **same shared media 5-tuple** the BUNDLE transport owns (ADR-011) — the allocation was
made on that socket during gathering, so its control round-trips and its relayed data share the socket
with STUN checks, DTLS, and RTP.

Two constraints shaped the design. First, the Rtp/media layer must not depend on the TURN module
(module boundary): TURN-aware assembly lives in the WebRTC composition layer, and the media session
receives only a protocol-agnostic binding of delegates. Second, the control plane must be safe to
share: a Refresh in flight next to a CreatePermission next to a ChannelBind must not corrupt each
other's transaction state or lose the server's rotated NONCE.

### Verified current state

- `TurnRelayCandidateSendPath` (`src/Core/Infrastructure/Turn/Client/TurnRelayCandidateSendPath.cs`)
  installs a permission **once per peer IP** via a `ConcurrentDictionary<IPAddress, Lazy<Task>>`, so
  concurrent checks to the same peer fire CreatePermission exactly once; a faulted entry is evicted so
  the next check retransmit retries rather than poisoning the peer. NONCE threading is serialised under
  a `SemaphoreSlim` (read → CreatePermission → write, no lost update). The shared permission task runs
  under `CancellationToken.None` (self-bounded by the transactor's RTO schedule); each caller observes
  its own token via `.WaitAsync(ct)`, so one caller cancelling cannot cancel a permission others depend
  on. `RefreshInstalledPermissionsAsync` re-issues CreatePermission for every known peer under the same
  gate (RFC 8656 §9, ~5 min lifetime).
- `TurnAllocationRefreshLoop` (`.../Turn/Client/TurnAllocationRefreshLoop.cs`) refreshes the allocation
  at **half the granted lifetime** (RFC 8656 §3.9 cadence — a lost refresh still has a second attempt
  before expiry), threading rotated REALM/NONCE and granted lifetime forward; a transient failure backs
  off `~5 s` and retries rather than abandoning the allocation; a server-returned lifetime 0 stops the
  loop (allocation gone, no teardown). On dispose it best-effort deletes the allocation with `Refresh(0)`
  bounded by a `~2 s` teardown timeout. The refresh transaction is **injected as a delegate**, so the
  loop is transport-agnostic and its clock/delay are injectable for deterministic tests.
- `WebRtcRelayBinding.CreateFactory` (`src/Core/Infrastructure/WebRtc/WebRtcRelayBinding.cs`) is the
  TURN-aware producer: given the transport's `targetedSend`, it assembles `TurnRelayIndicationChannel`
  + `TurnControlTransactor` (correlates responses by transaction id) + `TurnRelayControlClient` +
  `TurnRelayCandidateSendPath`, plus a `CompositeRelayKeepAlive` of the allocation-refresh loop and a
  `TurnPermissionRefreshLoop`, plus a `BindChannel` closure (ChannelBind at channel number `0x4000`
  with a `TurnChannelRebindLoop` at half the channel lifetime). It hands the media session only a
  `RelayIceBinding` (`Common/Relay`) of delegates — `indication`, `OnControlDatagram`, `SendAsync`,
  keepalive, `BindChannel`. The Rtp session never sees a TURN type.
- The control clients all share one authenticated `TurnControlTransactor` over the shared socket. The
  recon note in `4c-4b` confirmed the transactor keys pending round-trips by transaction id
  (`ConcurrentDictionary<txnId, TCS>`), so a Refresh next to a CreatePermission next to a ChannelBind
  is safe on one client — no second auth chain needed.
- Gathering side: `TurnAllocationProbe` allocates on the already-bound media socket during
  `WebRtcPeerConnection.GatherCandidatesAsync`, and the first successful allocation is *retained*
  (`_gatheredRelay`, keyed to the socket 5-tuple) so it survives the pre-start → post-start socket
  handover into the transport.

## Decision

Assemble the whole TURN control stack — allocate, permission, channel-bind, and both refresh loops —
in the WebRTC composition layer over one shared, authenticated control transactor riding the media
socket, and expose it to the media session as a protocol-agnostic `RelayIceBinding` of delegates.

1. **One shared authenticated control transactor.** Allocation refresh, per-peer permissions, and
   channel-bind/rebind all go through the same `TurnControlTransactor`, correlated by transaction id.
   Credentials (REALM/NONCE) are threaded forward across operations; the server's NONCE rotation is
   carried through rather than re-authenticated per call.
2. **Permission is per peer IP, installed lazily and refreshed.** CreatePermission fires once per peer
   IP (RFC 8656 §9, port ignored), deduplicated under concurrent checks; a `TurnPermissionRefreshLoop`
   re-installs before the ~5 min lifetime so a long-lived relay path does not start dropping inbound.
3. **Allocation refresh at half the granted lifetime, teardown on dispose.** `TurnAllocationRefreshLoop`
   keeps the allocation alive (RFC 8656 §3.9) and best-effort deletes it with `Refresh(0)` on disposal
   so a torn-down session does not leak a server-side allocation.
4. **The injected-delegate seam keeps the media layer TURN-free.** Refresh, permission, channel-bind,
   and raw-send are all delegates; the producer lives in `Infrastructure/WebRtc` (may depend on TURN),
   the media session receives only `Common/Relay` abstractions. This is enforced by the architecture
   tests' module-boundary gate.
5. **Dispose ordering is a composition-layer contract.** Every control path that rides the socket
   (permission install, refresh, keepalive teardown) must complete before the transport is disposed:
   ICE is drained first (no new checks), then the keepalive is disposed (running its `Refresh(0)` over a
   still-live transport), then the transport.

### Crux

The whole stack is *delegates over one socket*, not a second network client. Because the allocation is
made on the very socket the transport later owns, the control transactor's send is the transport's
targeted send, and the transport routes the server's control responses back via the binding's control
sink. That single shared, transaction-id-correlated transactor is what lets refresh, permission, and
channel-bind coexist without separate auth chains — and it is what keeps the Rtp layer off the TURN
module: the media session only ever holds `RelayIceBinding` delegates.

## Consequences

Positive: the relay allocation stays alive for the call and is cleaned up on teardown; permissions
survive their lifetime; the control plane is safe to share; the module boundary holds (verified by the
architecture gate). All of it is deterministically testable — clocks/delays and the refresh/send
transactions are injected — and was exercised against a fake TURN server over loopback (Refresh with
`LIFETIME`, teardown `Refresh(0)`, CreatePermission with valid MESSAGE-INTEGRITY from allocation
credentials, keepalive start-on-start / dispose-on-teardown).

Divergence and honest open edges:

- **Dispose latency under a dead relay.** Teardown `Refresh(0)` is bounded by `_teardownTimeout`
  (`~2 s`); against an unreachable relay server, disposal can take up to that bound. Bounded, but real.
- **Separate credential copies self-heal via 438.** The keepalive loop and the permission send path
  thread *separate* credential copies from the same allocation; a NONCE rotation in one is picked up by
  the other only via a 438 (stale nonce) round-trip. Pre-existing pattern, acceptable, but noted.
- **Permission install can outlive ICE drain (theoretical).** The shared permission task runs under
  `CancellationToken.None`; a `targetedSend` could in principle fire after transport dispose. Mitigated
  by the dispose ordering (ICE drained first), but the theoretical window is only closed once verified
  under a real end-to-end teardown — not yet done.
- **No real-server end-to-end run.** All verification is against a fake TURN server; the full stack has
  not been run against a production TURN server, and no browser interop exists (ADR-009).
- **Multi-TURN adoption is partial.** Only the *first* successful allocation is retained for adoption;
  additional configured TURN servers still emit a gathered candidate but are not adopted for relay.
- **TCP/TLS relay data path is out of scope here.** This stack is UDP-relay. The TCP/TLS *control*
  path is proven separately, but a persistent stream relay data path is a large, unbuilt feature.

## Guardrails

- The media session must receive only the `Common/Relay` binding, never a TURN type — the producer
  (`Infrastructure/WebRtc`) is the only place the TURN client stack is assembled (module-boundary gate).
- One shared authenticated control transactor per allocation; correlate by transaction id, thread NONCE
  forward. Do not spin a second auth chain for refresh or channel-bind.
- Dispose order is fixed and load-bearing: **ICE → keepalive (teardown Refresh(0)) → transport**. The
  keepalive teardown rides the transport's send, so the transport must still be alive.
- Permission is keyed by peer IP (port ignored); dedup installs per IP; evict a faulted entry so a
  retransmit retries; refresh before the ~5 min lifetime.
- Keep the refresh cadence at half the granted lifetime and the teardown bounded — do not block disposal
  on an unreachable server.

## Sources

- Logs (`docs/archive/agent-log/`):
  - `2026-07-19-dev-relay-4c-4a.md` — `TurnAllocationRefreshLoop` (refresh@lifetime/2, backoff retry,
    lifetime-0 stop, bounded teardown Refresh(0), injected refresh delegate + injectable clock).
  - `2026-07-19-dev-relay-4c-4b.md` — keepalive wired into the session lifecycle via `IRelayKeepAlive`;
    shared control client safe (transactor keyed by txn id); dispose order ICE → keepalive → transport;
    separate-credential-copy 438 self-heal note.
  - `2026-07-19-dev-relay-4d-3b-2b-i.md` — `TurnRelayCandidateSendPath` per-IP permission dedup,
    SemaphoreSlim NONCE threading, `CancellationToken.None` shared-permission + per-caller `.WaitAsync`.
  - `2026-07-19-dev-relay-4d-3b-2b-ii-B.md` — `WebRtcRelayBinding.CreateFactory` assembling the stack;
    MESSAGE-INTEGRITY from allocation credentials (RFC 8656 §9 / RFC 5389 §10.2).
  - `2026-07-19-dev.md` (slice 4d-2b) — `TurnAllocationProbe` gathering on the media socket; first
    allocation retained for post-start adoption.
- Code:
  - `src/Core/Infrastructure/Turn/Client/TurnRelayCandidateSendPath.cs`
  - `src/Core/Infrastructure/Turn/Client/TurnAllocationRefreshLoop.cs`, `TurnPermissionRefreshLoop.cs`,
    `TurnChannelRebindLoop.cs`, `TurnControlTransactor.cs`, `TurnRelayControlClient.cs`
  - `src/Core/Infrastructure/WebRtc/WebRtcRelayBinding.cs`
  - `src/Core/Infrastructure/Common/Relay/` (`RelayIceBinding.cs`, `IRelayKeepAlive.cs`,
    `CompositeRelayKeepAlive.cs`, `IRelayIndicationChannel.cs`)
- Markers / RFC: RFC 8656 §3.9 (allocation lifetime/refresh), §7 (Allocate), §9 (CreatePermission),
  §10 (Send/Data), §11–12 (ChannelBind/ChannelData), RFC 5389 §10.2 (long-term credential /
  MESSAGE-INTEGRITY). Adjacent: ADR-C16-01 (relay as ICE candidate), ADR-C16-03 (post-nom transport
  transition), ADR-011 (shared BUNDLE socket), ADR-009 (interop boundary).
