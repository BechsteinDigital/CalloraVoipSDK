# ADR-073: Stream Relay Transport (TURN over TCP/TLS ChannelData)

Status: Accepted
Date: 2026-08-18
Related: #240 (acceptance criterion 1), ADR-054, ADR-056, ADR-011, ADR-009
Ratified: 2026-08-18 (User) — three decisions and the slice plan accepted; reconnect = fail-and-renominate.

## Context

The TURN relay data path is UDP-only. `#240` needs the relay data path to also run over a TCP/TLS
connection to the TURN server (RFC 8656 §2.1 client-to-server transport, ChannelData framed over the
stream per §12.5). This ADR is that issue's first acceptance criterion — the design that must be
ratified before any code touches the shared, security-critical media transport.

### What already exists (verified)

- **Control over TCP and TLS works.** `TcpTlsTurnControlE2eTests` allocates against a hosted stream-transport
  `TurnServer` over TCP and TLS — Allocate/Auth/Refresh run.
- **Stream wire framing exists.** `TurnStreamFramer` reads STUN and ChannelData frames off a stream, including
  the RFC 8656 §12.5 4-byte padding. It is not wired into a media transport.
- **The relay control stack is already transport-agnostic.** `TurnRelayCoordinator` runs
  Allocate → CreatePermission → ChannelBind and installs the bound channel by driving `IRelayControlTransport`
  (`SendControlAsync` + `SetRelayChannel`) and `IRelayDatagramChannel` — abstractions deliberately kept off the
  concrete transport (their own docs say so). This cluster is used only in tests today.
- **The relay is modelled as an ICE candidate, not a parallel stack (ADR-054).** `IceLocalCandidate.Check` is
  the abstraction point; the relay's send path (`TurnRelayCandidateSendPath`) "holds no socket and no transport
  type, so it composes above any media transport." Nomination, consent and pair-priority are shared with
  host/srflx.

### What is UDP-bound and blocks a stream relay

- **`BundledMediaTransport` is built around a UDP send target** and, for relay, transitions *in place* into
  whole-socket ChannelData mode on the same 5-tuple (ADR-056). ADR-056 explicitly scopes TCP/TLS out:
  "This is UDP whole-socket relay; a stream relay transport is a separate, unbuilt feature."
- **`TurnAllocationProbe` binds a UDP media socket** and allocates on its 5-tuple, because ICE gathering runs
  before the transport takes the socket over. A stream relay has no such socket.

## The crux

Over UDP there is **one** shared media socket. Direct (host/srflx) and relay share it; ADR-054 makes relay a
send path on that socket, and ADR-056 transitions that same socket into ChannelData mode after a relay pair is
nominated. **A stream relay breaks this "one shared socket" premise**: the persistent TCP/TLS connection to the
TURN server *is* the transport, and it does not share a 5-tuple with the UDP media socket the direct candidates
use. So a stream relay cannot be an in-place *mode* of `BundledMediaTransport` (there is no socket to flip) —
it must be a **distinct media transport**, selected by ICE nomination, whose receive path is the stream rather
than the UDP socket. That is the load-bearing decision this ADR asks to ratify.

## Decision

### 1. Reuse the transport-agnostic relay control cluster; do not build a parallel stack

`TurnRelayCoordinator` / `IRelayControlTransport` / `IRelayDatagramChannel` / `TurnStreamFramer` were built
transport-agnostic precisely for this. The stream relay is a **new transport implementation behind these
seams**, not new orchestration:

- The stream transport implements `IRelayControlTransport`: `SendControlAsync` writes a STUN request as a
  stream frame; `SetRelayChannel` opens the ChannelData data phase.
- `TurnRelayCoordinator` drives Allocate → Permission → ChannelBind over that surface, unchanged.
- The bound channel is an `IRelayDatagramChannel` that frames/unwraps ChannelData over the stream via
  `TurnStreamFramer`.

This resolves #240's explicit open question ("reuse or deliberately discard the coordinator cluster"):
**reuse.** The cluster's test-only status ends here — this is the production caller it was shaped for.

### 2. The stream relay is a distinct media transport, chosen as an ICE candidate

Keep ADR-054 at the ICE layer — the send path is still the candidate — but the stream relay's send *and
receive* live on the stream, not the shared UDP socket:

- A new `StreamRelayMediaTransport` owns the persistent TCP/TLS connection, presents the same
  send/receive-datagram surface to DTLS/ICE/RTP (so those layers compose unchanged), and carries relayed media
  as ChannelData both directions.

  **This is libwebrtc's model.** In `p2p/base/turn_port.cc`, `TurnPort::PrepareAddress` creates its *own*
  socket per TURN allocation — `CreateClientTcpSocket(... server_address_ ...)` for a TCP/TLS server,
  `CreateUdpSocket` for UDP — so every TURN transport (UDP and stream alike) is a separate `Port` with its own
  connection to the server, not a mode of the host socket. Decision 2 converges the stream relay onto exactly
  that structure. (Our *existing* UDP relay diverges: ADR-056 transitions the one shared media socket in place
  rather than owning a separate TURN socket — a deliberate past choice, shipped, and out of scope for #240. The
  stream relay does not, and cannot, inherit that in-place model, so it lands closer to libwebrtc than the UDP
  path does.)
- Its relay candidate's `Check` send path frames a connectivity check as a Send indication over the stream
  (RFC 8656 §10, check phase), exactly as the UDP relay candidate does over the socket — only the raw-send
  delegate differs (stream write vs. `SendToAsync`).
- On nomination of the stream relay pair, steady-state media is ChannelData over the stream (RFC 8656 §11–12).
  Unlike ADR-056 there is **no in-place transition of a UDP socket** — the stream transport was ChannelData-
  capable from the start; nomination simply selects it.

### 3. Reconnect: fail-and-renominate for v1, not transparent continuity

A TCP/TLS connection can drop, and a TURN allocation is bound to the client transport address (the connection's
5-tuple). A new connection is a **new allocation with a new relayed address** — it cannot silently resume the
old one. So transparent reconnect is not achievable without re-gathering and re-nominating.

**v1 decision: a dropped stream relay fails ICE consent (RFC 7675) and the pair goes down**, exactly as ADR-056
treats a committed relay it cannot re-transition. The call then falls to another working pair, or the
application triggers an ICE restart (ADR-072, already shipped) which re-gathers — including a fresh stream
relay allocation — and re-nominates. Graceful reconnect that re-allocates and re-advertises a new relay
candidate under a live session is a **named follow-up**, not v1.

Rationale: this keeps the first cut honest and bounded. The reconnect machinery (re-allocate + re-candidate +
consent hand-off under load) is its own hard problem; pretending a stream relay is as durable as a UDP socket
would be the dishonest shortcut.

**Reference parity confirms this is not a shortcut but the established behaviour.** Neither reference stack
transparently reconnects a dropped TURN-TCP/TLS allocation:

- **libwebrtc** (`p2p/base/turn_port.cc`): `TurnPort::OnSocketClose` — "Connection with server failed" — calls
  `Close()`. The port fails; `P2PTransportChannel` continues with remaining candidates or the application
  performs an ICE restart. No allocation-preserving reconnect.
- **pjnath / pjsip** (`pjnath/src/pjnath/turn_sock.c`): a TCP or TLS close in `on_data_read` calls
  `sess_fail("TCP connection closed" / "TLS connection closed")` → `pj_turn_session_destroy`. The session is
  destroyed; recovery is via ICE restart.

So fail-and-renominate is *equal* to libwebrtc and pjsip (the project's match-or-exceed bar); graceful
reconnect would exceed both references at a cost neither pays, and our ICE restart (ADR-072) already provides
the recovery path they rely on.

### 4. Gathering produces a stream-relay candidate over its own connection

A stream analog of `TurnAllocationProbe` connects TCP/TLS to the server, allocates over that connection, and
yields a relay candidate whose retained allocation (relayed endpoint, effective credentials, and the live
stream) is handed to the coordinator to continue (permission / channel-bind / refresh) on the **same
connection** — no re-allocation, mirroring the UDP probe's socket-continuity contract. A failed allocation
yields no relay candidate and is not fatal to gathering (as with srflx and the UDP relay).

## Consequences

Positive:

- The relay data path becomes transport-independent at the seam that already models it that way; the UDP path
  is untouched (additive — no `StreamRelayMediaTransport` in play means byte-identical behaviour).
- "Force relay over TCP/TLS" remains an ICE policy over shared nomination machinery (ADR-054), not a bespoke
  code path.
- TLS gives a relay that traverses firewalls allowing only outbound 443 — the last-resort connectivity TURN
  exists for, now over the transport most restrictive networks permit.

Divergence and honest open edges (to be carried in the implementing PRs, per ADR-009):

- **No in-place UDP↔stream switch.** Direct is UDP, stream relay is a separate transport; a call does not
  migrate a live UDP socket onto a stream. Nomination picks one.
- **Reconnect is fail-and-renominate, not transparent** (decision 3). A mid-call server-connection drop needs
  an ICE restart to recover a relay.
- **Controlled-agent relay gap carries over** (ADR-054/056): only the controlling agent nominates a relay pair.
- **No browser-interop proof** (ADR-009): real-server and browser validation belong in the interop matrix
  (#228 / #240's later criteria), not in unit scope.

## Ratification points (for the User / PO)

1. **Distinct stream transport vs. in-place mode** (decision 2). Confirm the stream relay is a separate
   transport selected by nomination, not a mode of `BundledMediaTransport`. This is the load-bearing call.
2. **Reconnect semantics** (decision 3). Confirm fail-and-renominate for v1, with graceful re-allocation as a
   follow-up — or require graceful reconnect in scope now.
3. **Reuse the coordinator cluster** (decision 1). Confirm reuse over a fresh stack.

## Proposed slice plan (after ratification)

1. `StreamRelayMediaTransport` implementing `IRelayControlTransport` over a `TurnStreamFramer`-framed
   connection; unit-tested against the hosted stream `TurnServer` (control phase + a stream `IRelayDatagramChannel`).
2. Stream `TurnAllocationProbe` analog → a stream relay candidate; gathering wiring.
3. Relay candidate over the stream in the ICE nomination path (ADR-054 shape, stream send delegate).
4. Data path E2E over TCP against a real `TurnServer` (#240 criterion 2); then TLS (criterion 3).
5. Interop-matrix entry recording which combination is proven (#240 criterion 5).

## Implementation status

Slices 1–3 shipped incrementally (`StreamRelayMediaTransport`, `TurnStreamAllocationProbe`, the relay-candidate
send path over the stream, the inbound Data-indication path, the consent integration, the gathering-time
producer). The **media path** (decision 2 — the distinct transport selected by nomination) then shipped as one
change:

- **Per-candidate nomination routing.** `IceLocalCandidate` carries its own `SendVia` and an `OnNominated` hook,
  so coexisting relays (a UDP relay and a stream relay) are no longer conflated by a single shared field — the
  winning candidate's own transport is switched.
- **The switch itself.** `BundledMediaTransport.EnterStreamRelayMode` forwards every send to the stream
  transport's ChannelData path (its own TCP/TLS connection) and `InjectRelayedInbound` feeds its relayed inbound
  into the same pipeline; DTLS/SRTP/RTP ride above, unchanged. This is **libwebrtc's / pjnath's model** — a TURN
  allocation over its own socket, selected by nomination — reached here for the stream relay (the UDP relay keeps
  its ADR-056 in-place socket switch). `BundledStreamRelayPath` owns the adoption, the one-shot transition
  (ChannelBind → `EnterStreamRelayMode`) and the teardown order.
- **Peer wiring.** `WebRtcStreamRelayConnector` connects a TCP/TLS TURN entry; `WebRtcStreamRelayStore` retains it
  first-wins and adopts it into the session (now for the answerer, on build for the offerer). The old "TCP/TLS
  gathers no relay candidate" config trap is gone.

Open edges carried forward (ADR-009):

- **Controlled-agent stream relay.** The controlled agent (answerer) nominates via the session-level callback, not
  the per-candidate `OnNominated`, so an answerer-side stream relay does not switch its own media — the
  controlling (offerer) stream relay is the working path (the pre-existing controlled-agent relay gap, ADR-054).
- **Real-server / browser data-path E2E** stays interop (#228): the transition is timeout-bound (a relay only
  wins when direct fails) and not fully unit-testable, so the unit proof drives the transition through the real
  session ICE agent with an echoing attachment, while the transport ChannelData round-trip and the TCP/TLS
  connect + gather are proven against real sockets and a hosted `TurnServer`.
- **TLS certificate validation** is an injected callback (platform default in production); wiring it to
  configuration is a follow-up.

## Sources

- Code: `TurnStreamFramer`, `IRelayControlTransport`, `TurnRelayCoordinator`, `IRelayDatagramChannel`,
  `TurnAllocationProbe`, `BundledMediaTransport`, `TurnRelayCandidateSendPath`, `IceLocalCandidate`.
- ADRs: ADR-054 (relay as ICE candidate), ADR-056 (UDP whole-socket relay transition — scopes stream out),
  ADR-011 (shared BUNDLE transport), ADR-072 (ICE restart), ADR-009 (interop boundary).
- RFC 8656 §2.1 (TCP/TLS client-server transport), §10 (Send/Data indications), §11 (ChannelBind),
  §12 / §12.5 (ChannelData and its stream framing/padding).
