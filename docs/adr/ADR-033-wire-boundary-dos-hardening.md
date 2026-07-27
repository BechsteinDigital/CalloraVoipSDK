# ADR-033: DoS Hardening at the RTP and SIP Wire Boundaries

Status: Accepted
Date: 2026-07-09

## Context

Two untrusted wire boundaries can be driven by a remote peer into a denial of service if the
parser is not defensive:

1. The **RTP receive loop** demuxes each datagram (RFC 7983: STUN/DTLS/RTP/RTCP share the media
   5-tuple) and hands RTP/RTCP to `SrtpContext.Unprotect` / `UnprotectRtcp`. A datagram that
   passes the version/PT demux but is shorter than `12 + auth-tag` makes `Unprotect` throw
   `ArgumentException("SRTP packet too short.")`. The RTP branch caught
   `SrtpAuthenticationException`, `SrtpReplayException`, and `CryptographicException` — but
   **not** `ArgumentException`. A single 12..21-byte runt demuxed as RTP therefore escaped
   uncaught, terminated the receive loop, and killed all inbound media for the call — a
   remote-triggerable DoS, pre-existing and not introduced by the SRTCP work that found it.
   It went unnoticed because the existing malformed-packet test sent 40 B (past the length
   guard, failing on auth, which *was* caught) and so never exercised the short path.
2. The **SIP stream framer** buffers TCP/TLS bytes until it can frame a message. Without hard
   limits, a peer that never sends a `CRLFCRLF` terminator, or advertises a huge Content-Length,
   forces unbounded memory growth. The 64-KB SIP *message-parser* limit only applies *after*
   framing, so the framer must bound its own buffer.

The governing rule is ENGINEERING_RULES **K4**: untrusted remote input is parsed under a
`Try*`/null or drop contract (decode never throws out of the loop — HARD-G3, failures
observable not silent), and **DoS caps at every wire boundary** (message sizes, attribute
counts, frame limits) are mandatory for new parsers/listeners.

### Verified current state (graphify + code)

- **The RTP branch now drops all `Unprotect` throws.** `RtpSession.ProcessDatagram`
  (`src/Core/Infrastructure/Rtp/Session/RtpSession.cs` L649-675) wraps `inboundSrtp.Unprotect`
  in `catch (SrtpAuthenticationException)` → drop, `catch (SrtpReplayException)` → drop, and
  `catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException)`
  → LogDebug + drop. The comment (L665-671) records both the short-packet case (`< 12 + auth-tag`)
  and the `ObjectDisposedException` teardown race (a receive racing session dispose while the
  DTLS attachment already zeroed the keys).
- **The SRTCP branch mirrors it exactly.** The RTCP arm (L568-598) has the identical
  `catch … when (ex is ArgumentException or CryptographicException or ObjectDisposedException)`,
  plus a separate guard when the RTCP compound decode itself throws
  (`catch … when (ex is ArgumentException or NotSupportedException)`, L607-611) so a malformed
  compound is dropped, not fatal.
- **The secondary-stream branch mirrors it too.** `ProcessSecondaryDatagram` (L798-848) applies
  the same three-way fail-closed drop for the RTX/secondary SRTP context — parity is preserved
  across all three inbound crypto paths.
- **The framer enforces hard header and body caps.** `SipWireStreamFramer`
  (`src/Core/Infrastructure/Sip/Transport/SipWireStreamFramer.cs`) defines
  `DefaultMaxHeaderBytes = 64 * 1024` and `DefaultMaxBodyBytes = 256 * 1024` (L15-16). An
  unterminated header past the limit throws before buffering more (L72-74); a framed header over
  the limit throws (L79-81); a Content-Length over `_maxBodyBytes` throws (L94-96); chunked
  transfer-encoding is rejected (L88); a stream message with no Content-Length is rejected
  (L92-93). Each cap makes `TryReadFrame` throw so the transport tears the connection down
  instead of buffering unbounded memory (class comment L12-14).
- **The user-space datagram buffer is a fixed cap decoupled from the kernel queue.**
  `MediaSocketDefaults` (`src/Core/Infrastructure/Common/Network/MediaSocketDefaults.cs`) keeps
  `DatagramBufferBytes = 8192` (the per-receive user buffer, MTU-bounded with headroom)
  separate from `SocketReceiveBufferBytes = 1 MiB` (the SO_RCVBUF kernel queue) — the in-code
  comment records that conflating them previously undersized SO_RCVBUF at 8 KiB.

## Decision

Treat every inbound wire boundary as hostile and cap it in place:

1. **Every SRTP/SRTCP unprotect throw is a clean drop.** The RTP, RTCP, and secondary-stream
   branches each catch `SrtpAuthenticationException`, `SrtpReplayException`, and
   (`ArgumentException | CryptographicException | ObjectDisposedException`) → LogDebug + drop.
   A too-short or malformed datagram, and a receive racing teardown, never terminate the loop.
2. **The SIP framer bounds its own buffer.** Header and body byte caps, chunked reject, and
   mandatory Content-Length make `TryReadFrame` throw so the connection is closed rather than
   buffering unbounded memory.

**Crux:** the receive loop is the single most attractive DoS target — one uncaught throw kills
all inbound media for the call — so the failure mode for *any* malformed inbound packet is
observable-drop, never propagation; the fix is defined as parity across all three inbound crypto
branches, not a one-off patch of the branch that happened to be reported.

## Consequences

Positive:
- A 12-byte RTP runt is dropped and the next valid SRTP packet still arrives via
  `PacketReceived` — pinned by `SrtpHardeningTests.Receive_loop_survives_short_srtp_packets`
  (12-byte runt → previously `ArgumentException` → loop kill; now drop, then a positive
  assertion that a subsequent valid packet is delivered; the test is red without the fix, via
  timeout). The SRTCP runt path is covered by its mirror test.
- The framer rejects an unterminated/oversized header, an oversized body, chunked encoding, and
  a missing Content-Length — each an explicit `SipWireStreamFramerTests` case — so a stream peer
  cannot force unbounded buffering.

Tradeoffs / honest divergence:
- **Silent-catch rule interaction.** These catches all LogDebug, so they satisfy
  ENGINEERING_RULES R5 (no silent catch). LogDebug (not Warn) is a deliberate choice: a malformed
  inbound packet is expected background noise on an open UDP port and must not become a
  log-amplification vector — consistent with K4's amplification-suppression intent, but it means
  a real attack is only visible at debug verbosity.
- **Caps are fixed defaults, not yet configurable.** `DefaultMaxHeaderBytes` / `DefaultMaxBodyBytes`
  are constructor defaults and `DatagramBufferBytes` is a constant; there is no public knob to
  tune them per deployment. Adequate for the current threat model; a config surface is a possible
  follow-up, not built.
- **The short-packet fix was shipped as a one-line mirror of an already-approved SRTCP catch**
  (no separate review roundtrip). Honest scope note: correctness rests on that catch being
  byte-for-byte identical to the reviewed SRTCP arm plus the added survival test, not on an
  independent review of the RTP arm.
- Not security-audited; no external-interop/fuzzing claim — evidence is targeted unit tests over
  real sockets.

## Guardrails

- All three inbound crypto branches (RTP, SRTCP, secondary) drop
  `ArgumentException | CryptographicException | ObjectDisposedException` from unprotect and never
  propagate — parity is the invariant (RtpSession L568-598 / L649-675 / L798-848).
- A malformed RTCP compound decode is caught (`ArgumentException | NotSupportedException`) and
  dropped, never fatal.
- Every framer cap (header bytes, body bytes, chunked, missing Content-Length) makes
  `TryReadFrame` throw so the transport closes the connection — no unbounded buffering
  (ENGINEERING_RULES K4).
- The user-space datagram buffer cap stays decoupled from the kernel SO_RCVBUF request.
- Every drop is observable at LogDebug (ENGINEERING_RULES R5 / HARD-G3), tuned to avoid log
  amplification on an open port.

## Sources

- Logs: `docs/archive/agent-log/2026-07-09-dev-rtp-short-packet-dos.md` (short-packet RTP DoS,
  `ArgumentException` catch, `Receive_loop_survives_short_srtp_packets`),
  `2026-07-09-dev-b6-sip-framer-alloc.md` (framer caps context — same file that carries the
  framer allocation work in C07-01).
- Code (graphify-verified): `RtpSession.cs` (RTP drop L649-675, SRTCP drop L568-598, compound
  guard L607-611, secondary drop L798-848), `SipWireStreamFramer.cs` (caps L12-16, throws
  L72-96), `MediaSocketDefaults.cs` (`DatagramBufferBytes` / `SocketReceiveBufferBytes`).
- Markers / RFC: ENGINEERING_RULES K4 (wire-boundary DoS caps, Try/drop contract, amplification
  suppression / HARD-G3), R5 (no silent catch); RFC 7983 (media 5-tuple demux), RFC 3711 §3.3/§3.4
  (SRTP/SRTCP auth-tag length), RFC 3261 (SIP Content-Length framing), RFC 5626 §4.4.1
  (keepalive ping).
