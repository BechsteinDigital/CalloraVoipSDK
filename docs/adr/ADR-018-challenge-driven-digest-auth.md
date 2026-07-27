# ADR-018: Challenge-Driven Digest Authentication for REGISTER

Status: Accepted
Date: 2026-07-09

## Context

SIP authentication is challenge-driven (RFC 3261 §22): credentials are only needed to
answer a 401/407, never to *send* the initial request. Two mechanisms in the SDK forced a
password regardless of whether the registrar demanded one:

1. `SipAccount.Password` was a C# `required` member — every account construction forced a
   password.
2. `SipRegistrationService.ValidateRequest` rejected an empty password up front for the whole
   register family (register / unregister / unregister-all).

Separately, the real digest computation (`SipDigestAuthentication`, RFC 2617 / RFC 7616) and
the full 401→digest→retry→200 REGISTER chain were exercised only by a live registrar call —
all automated tests used the `NoopSipDigestAuthenticator`. A wrong digest formula would only
surface against a real PBX.

### Verified current state (graphify + logs)

- `SipRegistrationService` (`src/Core/Infrastructure/Sip/Signaling/Registration/SipRegistrationService.cs`)
  drives auth through the injected `ISipDigestAuthenticator`
  (`src/Core/Infrastructure/Sip/Authentication/ISipDigestAuthenticator.cs`); the register flow
  is `ExecuteRegisterAsync` (L84).
- `SipAccount.Password` is now `string … = string.Empty;` (no longer `required`); an empty
  password is a legal account.
- `ExecuteRegisterAsync` carries a guardrail: on `!authAttempted` **and** a 401/407 **and** an
  empty password, it throws `InvalidOperationException` ("… requires authentication, but no
  password is configured …") — deliberately *not* `SipRegistrationFailedException`, since this
  is a local configuration error, not a server-driven failure with a status.
- The real authenticator, the retry chain, session-timer policy, and route-set handling are
  pinned by known-answer / end-to-end tests (see Sources): MD5 and SHA-256 no-qop
  known-answers computed independently with `md5sum`/`openssl`; qop=auth response recomputed
  against the RFC 2617 §3.2.2.1 formula using the implementation's own emitted cnonce/nc.

## Decision

Treat credentials as challenge-scoped, not construction-scoped:

- Remove the `required` modifier and the up-front password rejection; an empty password is a
  valid account and a valid REGISTER attempt.
- Answer a 401/407 with a real digest via `ISipDigestAuthenticator`; retry once with the
  `Authorization` header.
- If the registrar challenges but no password is configured, fail *locally* and *clearly*
  (`InvalidOperationException`), distinct from a server-status failure
  (`SipRegistrationFailedException`).
- Lock the real digest math and the retry chain with known-answer + end-to-end tests instead of
  relying on a live registrar call for regression safety.

Crux: separate the three failure modes that were previously conflated — no-challenge (succeed
without a password), unanswerable-challenge (local config error), and rejected-credentials
(server failure) — and give each its own, type-distinguishable outcome.

## Consequences

Positive: registrars that don't challenge work with no password; challenge-driven auth is
RFC-correct; the unanswerable-challenge case gives an actionable local error instead of an
opaque server failure. The digest formula and 401→200 chain are now regression-locked without a
live call.

Tradeoffs: `Assert.ThrowsAsync<T>` distinguishes the two exception types precisely only because
`SipRegistrationFailedException : InvalidOperationException`; the guardrail relies on that
ordering. Known-answer coverage is MD5 / SHA-256 no-qop + qop=auth structure; MD5-sess /
SHA-512-256 / SHA-256-sess paths are covered only indirectly via algorithm resolution, not by
their own pinned vectors (noted follow-up). Stale-nonce and Proxy-Auth (407) retry variants are
not yet covered.

## Guardrails

- Never require a password to *send* a REGISTER; only to answer a challenge.
- Unanswerable challenge (401/407, no password) → local `InvalidOperationException`, not
  `SipRegistrationFailedException`.
- The real digest formula stays known-answer-pinned; a formula drift MUST break a pinned vector.

## Sources

- Logs: docs/archive/agent-log/2026-07-09-dev-b7-password-only-on-register.md;
  docs/archive/agent-log/2026-07-09-dev-b8-register-digest-retry.md;
  docs/archive/agent-log/2026-07-09-dev-b8-digest-auth-tests.md
- Code: `SipRegistrationService.ExecuteRegisterAsync`
  (src/Core/Infrastructure/Sip/Signaling/Registration/SipRegistrationService.cs);
  `ISipDigestAuthenticator` (src/Core/Infrastructure/Sip/Authentication/);
  `SipAccount.Password`
- Tests: SipRegistrationPasswordOptionalTests; SipRegistrationDigestRetryTests;
  SipDigestAuthenticationTests
- Marker: RFC 3261 §22; RFC 2617 §3.2.2.1; RFC 7616; B.7 / B.8 slices 1 + 4
