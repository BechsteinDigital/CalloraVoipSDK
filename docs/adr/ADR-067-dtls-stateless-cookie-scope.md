# ADR-067: Scope of the DTLS Stateless Cookie (HelloVerifyRequest)

Status: Accepted
Date: 2026-08-12

## Context

RFC 6347 §4.2.1 defines a stateless cookie exchange for the DTLS server role: on an unverified
`ClientHello` the server answers with a `HelloVerifyRequest` carrying a cookie derived from the client
address, and only a `ClientHello` that echoes that cookie earns the expensive certificate flight. Without
it, a single spoofed-source `ClientHello` makes the server sign and transmit a full certificate flight to
an address that never asked for it — the classic DTLS amplification vector.

`DtlsSrtpHandshaker` implements the exchange (`DtlsSrtpHandshaker.cs` — `HelloVerifyRequest` before the
server cert flight, commit `548a471d`). It is **not** unconditionally on: `DtlsMediaAttachment` carries a
`_useServerCookie` flag, defaulted **on** for the SIP entry point (`TryCreate`) and **off** for the
explicit/bundled entry point (`Create`) that the WebRTC path uses (commit `37610295`).

Two things about that split were only recorded as code comments, which is why review of the DTLS
sammelticket (#163) left it flagged rather than closed:

1. **Why the WebRTC path opts out at all.**
2. **That the opt-out keys on the *path* ("this leg has ICE") rather than on a *nominated* ICE
   generation**, which is the stricter reading of the acceptance criterion the finding was written
   against.

## Decision

The cookie is scoped to legs that have no other source validation, and the criterion for that is the
path, not the nomination state.

1. **SIP legs without ICE: cookie on.** A SIP media leg accepts DTLS records at a negotiated address
   with no prior connectivity check, so the `ClientHello` source is unverified. This is exactly the case
   RFC 6347 §4.2.1 exists for, and it stays on by default.

2. **WebRTC/ICE legs: cookie off, ICE is the source validation.** On the bundle path a DTLS record only
   reaches the association through a transport whose inbound source filter is pinned to the ICE-nominated
   remote endpoint. An attacker cannot deliver a spoofed `ClientHello` to the handshake without first
   completing an ICE connectivity check against a peer-supplied `ice-pwd` (RFC 8445 §7) — a stronger
   return-routability proof than the cookie, and it happens *before* any certificate work. Layering the
   cookie on top buys nothing and costs interop: some browser DTLS clients stall on a server-sent
   `HelloVerifyRequest` on an already-validated 5-tuple.

3. **The discriminator is the path, not the nominated generation.** The flag is fixed when the attachment
   is constructed; it does not consult "is there a nominated pair *right now*". In the real sequence the
   handshake only starts after nomination, so the two readings coincide — but the code does not enforce
   the ordering, and this ADR records that deliberately.

4. **The cookie's client id is snapshotted at handshake start.** When the cookie is on, it binds to the
   remote endpoint read once at handshake start. An ICE re-nomination inside the cookie window would bind
   to the stale address and time out. Accepted: only the opt-in SIP path uses the cookie, and that path
   never re-nominates (`UpdateRemoteEndPoint` is a bundle/ICE facility).

## Consequences

- **The amplification surface is closed on the path that has it** (SIP without ICE) and left to the
  stronger mechanism on the path that has one (ICE).
- **Browser interop is unaffected**: no `HelloVerifyRequest` is emitted towards a browser DTLS client.
- **The residual gap is explicit**: an internal caller that used `DtlsMediaAttachment.Create` for a leg
  *without* ICE source validation would silently get no cookie, because the default there is off. That is
  an internal API (`internal sealed class`) with two call sites, both on the bundle path; the guard is
  this ADR plus the parameter documentation, not a runtime assertion.
- **Reference parity**: libwebrtc/BoringSSL and pjsip likewise do not run a DTLS cookie exchange on ICE
  legs — the ICE check is treated as the return-routability proof there as well.

## Alternatives considered

- **Cookie unconditionally on.** Rejected: it re-verifies an address ICE already verified, and it stalls
  browser clients — a regression on the path that carries the WebRTC traffic, in exchange for no
  additional protection.
- **Tie the opt-out to a nominated ICE generation at handshake start** (assert nomination instead of
  assuming it). Deferred rather than rejected: it is the stricter reading and would turn the ordering
  assumption into an enforced invariant, but it needs a nomination-state seam from the ICE agent into the
  DTLS attachment that does not exist today. Tracked as follow-up, not as part of #163.
- **Cookie on for every server-role leg, with a browser allowlist.** Rejected: fingerprinting the peer
  stack to decide a security parameter is fragile and inverts the reasoning — the question is whether the
  source is already validated, not which client is on the other end.

## References

- RFC 6347 §4.2.1 — DTLS denial-of-service countermeasure (stateless cookie).
- RFC 8445 §7 — ICE connectivity checks (`ice-pwd`-authenticated return routability).
- ADR-066 — post-handshake association servicing; the same association, after key export.
- Issue #163 — DTLS review sammelticket; this ADR closes its documentation caveat.
