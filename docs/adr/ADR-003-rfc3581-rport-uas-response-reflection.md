# ADR-003: RFC 3581 §4 – rport/received Reflection in UAS Responses

**Status:** Accepted  
**Date:** 2026-04-09  
**RFC reference:** RFC 3581 §4 (Symmetric Response Routing)

---

## Context

RFC 3581 extends RFC 3261 to enable symmetric response routing for NAT traversal.
When a UAC includes an unvalued `;rport` parameter in the top Via header of a
request, the UAS MUST:

1. Fill in `rport=<actual-source-port>` with the UDP/TCP source port of the incoming packet.
2. Add `received=<source-IP>` when the source address differs from the Via sent-by host.

Without these two modifications, responses may be routed to the port/address the
UAC advertised in its Via (which may be its private/NATed address), rather than
to the actual address where it is reachable from the server's perspective.

Previously the SDK copied the Via header verbatim from the request into every UAS
response. The bare `;rport` was never filled in, and `received=` was never added.

---

## Decision

Add `SipProtocol.ReflectViaRport(string? viaHeader, IPEndPoint actualRemote)`.

The method:
- Returns the header unchanged when no bare `;rport` is present.
- Inserts `=<sourcePort>` immediately after `;rport`.
- Appends `;received=<sourceIP>` when the sent-by host (parsed from the Via
  `host[:port]` field) differs from `actualRemote.Address`.

Call `ReflectViaRport` in:
- `SipCallSignalingService.CreateIngressResponseHeaders` (early validation / provisional replies).
- `SipCallSessionHeaderService.CreateResponseHeadersFromRequest` (dialog-level UAS responses).

Both sites already have access to the remote endpoint (`remoteEndPoint` parameter
and `_context.RemoteEndPoint` respectively).

---

## Consequences

**Positive:**
- SIP clients behind NAT receive responses on the address/port they are actually
  reachable on, eliminating one major class of one-way-audio / no-ringback failures.
- Completes the RFC 3581 UAC + UAS pair: UAC already set `;rport` in outbound Via.
- Zero breaking change — the modification only fires when `;rport` (without value)
  is present.

**Negative:**
- Only applies when `remoteEndPoint` is passed through. Code paths where the UAS
  builds responses without a known remote endpoint (e.g., internally constructed
  responses not going through either helper) would miss the reflection. No such
  paths currently exist.

---

## Alternatives considered

1. **Add `remoteEndPoint` to every response builder parameter** — Rejected as
   unnecessarily invasive. The two entry points (`CreateIngressResponseHeaders`
   and `CreateResponseHeadersFromRequest`) cover all current UAS response paths.
2. **Reflect at the transport layer** — Rejected. Reflection is a SIP semantic
   concern (Via header modification), not a transport concern. Doing it at the
   transport layer would require the transport to understand SIP header structure.
