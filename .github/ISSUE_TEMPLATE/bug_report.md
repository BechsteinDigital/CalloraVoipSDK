---
name: Bug report
about: Report a defect in the SDK
title: ''
labels: bug
assignees: ''

---

<!--
Security vulnerability? Do NOT file it here — see SECURITY.md for private reporting.
Search existing issues first: this repo tracks known findings in issues #3–#20.
-->

**Describe the bug**
A clear and concise description of what is wrong.

**Affected area**
Which subsystem? (SIP · SDP · RTP/SRTP/DTLS · STUN/TURN/ICE · Core · Client/Audio · WebRTC)
File/class if known (e.g. `Srtp/Context/SrtcpContext.cs`).

**To reproduce**
Steps or, ideally, a minimal failing test. See `docs/maintainers/onboarding-debugging.md`
for the test harness (loopback, capturing transport, Asterisk container).

**Expected behaviour**
What you expected, ideally with the RFC/section if it's a compliance question.

**Environment**
- SDK version / commit:
- .NET target (net8.0 / net9.0 / net10.0):
- OS (Linux / Windows) and, for interop issues, the peer stack (Asterisk, FreeSWITCH,
  Fritz!Box, sipgate, browser, …):
- Transport (UDP / TCP / TLS / WS / WSS) and media mode (plain RTP / SDES-SRTP / DTLS-SRTP):

**Logs**
Relevant logs. Enable the SIP wire trace via `LogLevel.Trace` (SDES keys and ICE passwords
are redacted automatically). **Scrub any remaining credentials before posting.**

**Additional context**
Anything else that helps.
