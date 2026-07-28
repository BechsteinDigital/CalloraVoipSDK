# Interop matrix

CalloraVoipSdk implements standard SIP/SDP/RTP, so it is expected to interoperate with
any RFC-compliant PBX or trunk. This page is explicit about **what has actually been
verified** versus what is configuration guidance we have not yet run through a formal
interop test.

## Verification status

| Platform | Status | Notes |
|----------|--------|-------|
| **Asterisk** | ✅ Full SIP/RTP flow automated in CI | Real PJSIP Asterisk container (Testcontainers), **all cases green / none skipped**: register (happy + failure), in/outbound calls with live RTP, codec negotiation (PCMU/PCMA/G722), SRTP-SDES, DTMF (RFC 4733), hold/unhold, blind & attended transfer, session timers (RFC 4028), early media (RFC 3960), TCP/TLS transport, plus a two-leg bridged call with byte-exact bidirectional media. See the [Asterisk page](asterisk.md) |
| **Chromium** (WebRTC) | ✅ Automated in CI | Headless via Playwright: signalling → ICE → DTLS-SRTP → SRTP, bidirectional Opus and browser-decoded VP8, in **both** roles (SDK as offerer and as answerer). See the [browsers page](browsers.md) |
| **Mozilla Firefox** (WebRTC) | ✅ Automated in CI | The same three scenarios via the browser-agnostic `BrowserEngine` matrix. Negotiates DTLS-SRTP with **AES-CM** — the SDK needs no AES-GCM for Firefox interop |
| **coturn** (TURN relay) | ✅ Automated in CI | TURN relay allocation and media over a real coturn server, end-to-end |
| **AVM FRITZ!Box** | ✅ Verified against a live device (manual) | Register, dial, two-way audio, DTMF against real hardware; source of several hardening fixes. Not an automated CI test |
| **FreeSWITCH** | 🧪 Automated, run locally | The **two-leg scenario matrix** runs against a real FreeSWITCH container via the shared `IPbxFixture` abstraction: registration, bridged media (plain, SDES, codec-mismatch transcoding), byte-exact content, RTCP, hold/unhold, attended transfer, DTMF, concurrent-call soak. **Not in the PR CI gate** (trait `InteropFreeSwitch`) and narrower than the Asterisk matrix — no session timers, early media, TCP/TLS or codec-negotiation matrix. See the [FreeSWITCH page](freeswitch.md) |
| WebKit / Safari (WebRTC) | ⚙️ Not yet verified | The browser matrix has a WebKit engine, but the tests skip when the browser is not installed — see the [browsers page](browsers.md) |
| sipgate | ⚙️ Guidance only — not yet formally verified | Standard trunk registration expected to work; see the page |
| 3CX | ⚙️ Guidance only — not yet formally verified | Standard extension registration expected to work |

- ✅ **Automated (CI)** — a repeatable interop suite runs the full flow against a real container or
  browser on every relevant run.
- ✅ **Verified (manual)** — exercised against real hardware by hand; not reproducible in CI.
- 🧪 **Automated, run locally** — a repeatable suite exists and passes, but is not (yet) part of the
  CI gate, so regressions are caught only when it is run.
- ⚙️ **Guidance only** — the SDK speaks standard SIP and *should* interoperate, and we provide
  configuration notes, but we have **not** yet run a validated end-to-end test against that
  platform. Do your own acceptance test before relying on it in production.

## What "standard" support means

The SDK covers the interop-relevant basics broadly:

- Digest authentication (RFC 7616 / RFC 8760: MD5, SHA-256, SHA-512-256, `qop=auth-int`),
  bounded stale-nonce retry
- `Expires` precedence and registration refresh (RFC 3261 §10.2.1.1 / §10.3), RFC 5626 `;ob`
- Reliable provisionals (RFC 3262) only when the peer requires `100rel`
- Early media (RFC 3960): pre-answer receive-only media and DTMF in the early dialog
- Static payload types without `rtpmap`, ordered codec preference
- RTCP compound decoding tolerant of unknown packet types (e.g. RFC 3611 XR)
- SRTP/SRTCP via SDES (RFC 4568 / RFC 3711), offered and answered — the SRTCP auth tag is 80 bit
  for every suite (§6.2), which is what libsrtp-based peers expect
- DTLS-SRTP (RFC 5763) with AEAD-AES-GCM offered preferred (RFC 7714) and AES-CM as the fallback

## Reporting interop results

If you validate against a platform not marked verified here, interop reports are
welcome — contact [info@bechstein.digital](mailto:info@bechstein.digital). Verified
results get promoted on this matrix.
