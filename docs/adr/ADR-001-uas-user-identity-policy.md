# ADR-001: UAS User Identity Policy (RFC 3261 §8.2.2.1)

**Status:** Accepted  
**Date:** 2026-04-09  
**RFC reference:** RFC 3261 §8.2.2.1

---

## Context

RFC 3261 §8.2.2.1 states:

> "The UAS MUST inspect the Request-URI. If the Request-URI uses a scheme not supported by the UAS, the UAS SHOULD reject the request with a 416 (Unsupported URI Scheme) response. The UAS SHOULD also check the value of the To header field in the request. [...] If the user identified by the To header field is not known at this UAS, the UAS SHOULD respond with a 404 (Not Found) response."

The keyword is **SHOULD** (not MUST), meaning a conformant implementation may choose to accept all users and let the application layer decide. In many deployment scenarios (softphone, embedded phone, B2BUA) the SIP proxy has already validated the target address before forwarding — so UA-level user validation is redundant.

However, for deployments where the UA listens directly on the network (no proxy), user validation at the UA level prevents unauthorized session establishment.

---

## Decision

Introduce a pluggable `ISipUasUserIdentityPolicy` interface injected into `SipCallSignalingService`:

```csharp
public interface ISipUasUserIdentityPolicy
{
    bool IsServedUser(string requestUri);
}
```

- **Default:** `AcceptAllSipUasUserIdentityPolicy` — always returns `true` (existing behavior preserved, backwards compatible).
- **Custom:** Callers register an implementation via DI to restrict which Request-URIs are accepted.
- When `IsServedUser` returns `false`, the UAS responds with **404 Not Found** after sending the mandatory **100 Trying** (§8.2.6.1).

The policy receives the **raw Request-URI** from the inbound SIP request, not the To header, because the Request-URI is the actual routing target per §8.2.2.1.

---

## Consequences

**Positive:**
- RFC 3261 §8.2.2.1 SHOULD requirement satisfied.
- Zero breaking change — existing code without explicit policy injection continues to accept all INVITEs.
- Policy is independently testable and injectable.

**Negative:**
- Adds one optional constructor parameter to `SipCallSignalingService`; DI container must register it when needed.
- The check fires after `100 Trying` is sent, which is correct per §8.2.6.1 but means the remote party sees a brief provisional before the rejection.

---

## Alternatives considered

1. **Hard-coded user list** — Rejected. Not flexible enough for SDK consumers.
2. **Check at application layer** — Rejected. §8.2.2.1 places the obligation on the UAS, not the application. The policy belongs in the protocol stack.
3. **Always reject unknown users using a configured username** — Rejected. The `SipCallSignalingService` does not own account configuration; coupling it to registration state would violate DDD layer separation.
