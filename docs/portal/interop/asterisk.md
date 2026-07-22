# Asterisk

**Status: 🧪 REGISTER covered by an automated interop test (CI).** `AsteriskRegisterInteropTests`
runs the 401→200 REGISTER flow against a PJSIP Asterisk container in CI, so registration against
`chan_pjsip` is exercised on every relevant run. Full call/media/DTMF/transfer are **not** yet
covered by an automated test — run your own acceptance test for those before production.

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
