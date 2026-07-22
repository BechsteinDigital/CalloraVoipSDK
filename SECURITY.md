# Security Policy

CalloraVoipSdk implements security-sensitive protocols — SIP digest authentication,
SRTP/SRTCP, DTLS-SRTP, and STUN/TURN authentication. We take vulnerability reports
seriously and appreciate responsible disclosure.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Report privately through either channel:

- **Email:** info@bechstein.digital — put `SECURITY` in the subject line.
- **GitHub private advisory:** the *Security* tab → *Report a vulnerability*
  (GitHub's private vulnerability reporting, if enabled for this repository).

Please include, as far as you can:

- The affected component or file (e.g. `Srtp/Context/SrtcpContext.cs`) and version/commit.
- A description of the issue and its impact (e.g. authentication bypass, key leakage,
  denial of service, spoofing).
- Steps to reproduce or a proof of concept — a failing test on the appropriate level
  (see `docs/maintainers/onboarding-debugging.md`) is ideal.
- Any suggested remediation.

## What to expect

- **Acknowledgement** of your report within **3 business days**.
- An initial **assessment** (severity, affected versions) within **10 business days**.
- We will keep you informed of progress and coordinate a disclosure timeline with you.
  We aim to release a fix before public disclosure and will credit you unless you prefer
  to remain anonymous.

## Scope

In scope: the SDK code in `src/` — SIP signaling and transport, SDP negotiation, RTP/RTCP,
SRTP/SRTCP, DTLS-SRTP, STUN/TURN (client **and** the bundled server hosting), ICE, and the
public `VoipClient` / `WebRtcClient` facades.

Out of scope: vulnerabilities in third-party dependencies (report those upstream — see
`THIRD-PARTY-NOTICES.md`); issues that require a malicious local operator or physical
access; and the explicitly documented preview limitations of the WebRTC facade
(not yet browser-validated; see `README.md`).

## Supported versions

This is a pre-1.0 / preview line (4.6.x). Security fixes land on the latest released
version. Please test against the most recent release before reporting.
