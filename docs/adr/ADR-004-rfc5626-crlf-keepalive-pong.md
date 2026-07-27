# ADR-004: RFC 5626 §4.4.1 – CRLF Keepalive Pong on Stream Transports

**Status:** Accepted  
**Date:** 2026-04-09  
**RFC reference:** RFC 5626 §4.4.1 (CRLF Keep-Alive Mechanism)

---

## Context

RFC 5626 §4.4.1 defines a lightweight keepalive mechanism for SIP over stream
transports (TCP, TLS):

> A client SHOULD send a double-CRLF (4 bytes: `\r\n\r\n`) when no message has
> been received for a configurable interval. A server receiving the double-CRLF
> SHOULD respond with a single CRLF (2 bytes: `\r\n`).

The double-CRLF is also valid as a leading preamble before a SIP message per
RFC 3261 §7.5. The distinction is:
- **Standalone** `\r\n\r\n` (no SIP message follows) → keepalive ping; pong required.
- `\r\n\r\n` followed by a SIP start-line → preamble; no pong.

Previously the SDK trimmed leading CRLF silently in `SipWireStreamFramer.TrimLeadingCrLf`
without ever responding. Clients relying on pong detection to verify server
liveness would time out and close the connection.

---

## Decision

**`SipWireStreamFramer`:**
- `TrimLeadingCrLf` now sets `_consumedKeepalivePing = true` when it removes ≥ 4
  bytes (at least one double-CRLF pair).
- Expose `bool ConsumedKeepalivePing` (read-only) and `void ClearKeepalivePingFlag()`.

**`SipStreamConnection.ReceiveLoopAsync`:**
- Count dispatched SIP frames per read cycle (`framesDispatched`).
- After the frame-dispatch loop, if `framesDispatched == 0 && _framer.ConsumedKeepalivePing`:
  send `\r\n` (single CRLF pong) and clear the flag.
- If `framesDispatched > 0`, the double-CRLF was a preamble; do NOT pong.

This CRLF pong is **not** sent for `SipWebSocketConnection` because WebSocket
has its own native Ping/Pong frames defined by RFC 6455 §5.5.

---

## Consequences

**Positive:**
- Clients using RFC 5626 keepalive (including most SIP softphones and ATAs behind
  NAT) can reliably detect that the server-side connection is alive.
- Minimal overhead: 2 bytes sent only when a keepalive is received, which is
  infrequent (default interval ≥ 120 seconds per RFC 5626).

**Negative:**
- The full RFC 5626 "Outbound" mechanism (reg-id, Instance-ID, flow recovery) is
  still not implemented. The pong is the keepalive-maintenance part; Outbound
  registration flow recovery is a separate and more complex feature.
- `SipWireStreamFramer` now has a side-effectful flag (`ConsumedKeepalivePing`)
  that callers must reset. Callers that forget `ClearKeepalivePingFlag` after a
  pong would not re-pong on the next keepalive (the flag is set again by the next
  `TrimLeadingCrLf` call, so practical impact is minimal).

---

## Alternatives considered

1. **Respond with pong always (including CRLF-prefixed SIP messages)** — Rejected.
   RFC 5626 §4.4.1 says to respond to the keepalive, not to preambles before SIP
   messages. Sending extra bytes before delivering a response would be incorrect.
2. **Handle in `SipWireStreamFramer.TryReadFrame` via a synthetic frame** — Rejected.
   Mixing keepalive semantics into the message framing layer violates separation of
   concerns; the framer should only produce SIP message frames.
3. **Handle at transport level (before framing)** — Rejected. At the raw byte level
   we cannot reliably distinguish a standalone `\r\n\r\n` from one that has more
   bytes following in the next network segment until framing is attempted.
