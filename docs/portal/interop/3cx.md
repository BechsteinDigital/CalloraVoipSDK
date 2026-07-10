# 3CX

**Status: ⚙️ configuration guidance — not yet formally verified.** CalloraVoipSdk registers
as a standard SIP extension and is expected to work with 3CX, but we have not yet run a
validated interop test. Run your own acceptance test first.

## Create an extension

In the 3CX console, add an extension and note:

- the **extension number** (the SIP user, e.g. `1001`)
- its **authentication ID** and **authentication password** (under the extension's
  *Options*/*Phone Provisioning* — 3CX distinguishes the auth ID from the extension
  number)
- the 3CX **FQDN/IP** as the registrar

## Connect from the SDK

```csharp
var connect = await client.ConnectAsync(new SipAccount
{
    Username  = "1001",                 // 3CX auth ID (may differ from the extension number)
    Password  = "auth-password",
    SipServer = "pbx.example.3cx.eu"    // your 3CX FQDN
});
```

> 3CX often uses a separate **authentication ID** distinct from the extension number. Use
> the auth ID as `Username`.

## Expected configuration

- **Codecs** — 3CX handles G.711 and Opus; keep `PCMU`/`PCMA` in `PreferredAudioCodecs`
  as a baseline.
- **DTMF** — RFC 4733 telephone-events, which the SDK sends.
- **SRTP** — 3CX supports SDES SRTP; pair its "SRTP enabled/required" setting with the
  matching [`SrtpPolicy`](../guides/srtp-srtcp.md).
- **NAT** — for off-LAN registration follow 3CX's SBC/STUN guidance; the SDK's ICE is
  opt-in and experimental.

## Validate

Registration, extension-to-extension dial, an external call via a 3CX trunk, DTMF, and
SRTP if enabled. Reports welcome: [info@bechstein.digital](mailto:info@bechstein.digital).
