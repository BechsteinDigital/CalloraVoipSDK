# Asterisk

**Status: ✅ Full SIP/RTP flow automated in CI.** The interop suite
(`tests/CalloraVoipSdk.InteropTests`) runs against a real PJSIP Asterisk container
(`andrius/asterisk`, via Testcontainers) on every relevant run:

- **Registration** — 401→200 happy path, plus failure paths (wrong password / unknown user →
  prompt `Failed`, unreachable → `Timeout`).
- **Calls** — outbound happy path (INVITE→200→ACK→**live RTP**→BYE), inbound (Asterisk→SDK),
  and failure paths (busy/486, decline/403, unknown/404, no-answer timeout, caller cancel).
- **Media** — codec negotiation (PCMU/PCMA/G722 by preference), SRTP-SDES (RTP/SAVP + `a=crypto`,
  encrypted media), DTMF (RFC 4733) receive + negotiation.
- **In-call** — hold/unhold (re-INVITE), blind & attended transfer (REFER/Replaces),
  session-timer negotiation (RFC 4028).
- **Early media** (RFC 3960) — pre-answer receive-only media from a 180/183 SDP (plain and
  SRTP-SDES) plus DTMF in the early dialog.
- **Transport** — UDP, TCP and TLS.
- **Two-leg bridged media** — a separate suite bridges two SDK legs through the PBX and verifies
  **bidirectional, byte-exact** media (RTP counters both ways, local + remote RTCP quality, and
  byte-identical PCMU payload A→B).

The suite currently runs with **all cases green and none skipped**. Still, run your own acceptance
test for anything you depend on before production.

## Example `pjsip.conf` peer

```ini
[6001]
type = endpoint
context = from-internal
disallow = all
allow = ulaw,alaw,opus
auth = 6001-auth
aors = 6001

[6001-auth]
type = auth
auth_type = userpass
username = 6001
password = your-strong-password

[6001]
type = aor
max_contacts = 1
```

## Connect from the SDK

```csharp
var connect = await client.ConnectAsync(new SipAccount
{
    Username  = "6001",
    Password  = "your-strong-password",
    SipServer = "asterisk.lan"     // Asterisk host/IP
});
```

## Expected configuration

- **Codecs** — match `allow=` on the endpoint with your `PreferredAudioCodecs`; keep
  `ulaw`/`alaw` for a safe baseline.
- **DTMF** — Asterisk defaults suit RFC 4733; keep `dtmf_mode = rfc4733` on the endpoint.
- **SRTP** — to require encryption set `media_encryption = sdes` on the endpoint and
  `SrtpPolicy.Required` on the SDK (see [SRTP/SRTCP](../guides/srtp-srtcp.md)).
- **Reliable provisionals** — the SDK only does `100rel` when the peer requires it; set
  `100rel = required` on the endpoint if you want PRACK.

## Validate

Registration, internal dial between two endpoints, DTMF into an IVR/`Read()`, and SRTP
if configured. Interop reports welcome: [info@bechstein.digital](mailto:info@bechstein.digital).
