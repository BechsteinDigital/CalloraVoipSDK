# FreeSWITCH

**Status: 🧪 automated interop suite, run locally.** FreeSWITCH is the second PBX behind the shared
`IPbxFixture` abstraction: the **two-leg scenario matrix** that runs against Asterisk also runs
against a real FreeSWITCH container (`tests/CalloraVoipSdk.InteropTests`, trait
`InteropFreeSwitch`).

The suite is **not part of the PR CI gate** — it is a local-first check, so regressions are caught
only when it is run explicitly:

```bash
dotnet test tests/CalloraVoipSdk.InteropTests -c Release --filter "Category=InteropFreeSwitch"
```

## What is verified against FreeSWITCH

Each of these runs against a real FreeSWITCH container, with two SDK legs bridged through the PBX:

| Scenario | Assertion |
|----------|-----------|
| Registration | A directory user registers over UDP through the fixture adapter |
| Bridged call | Both legs reach `Connected` |
| Media | RTP flows in **both** directions, with RTP counters on each leg |
| Media content | A marked PCMU payload arrives **byte-exact** A→B through the PBX |
| RTCP | Local quality metrics *and* the remote SR/RR report are populated |
| SRTP-SDES | An encrypted bridged call flows in both directions |
| Codec mismatch | Legs with different codecs still flow, via transcoding |
| Hold / unhold | Unhold resumes bidirectional media |
| Attended transfer | The callee is bridged to a new target, with media flowing after the transfer |
| DTMF (RFC 4733) | A digit traverses the bridge, caller → callee |
| Concurrent calls | A short concurrent-call soak: all calls connect and flow media |

## What is *not* covered against FreeSWITCH

These are exercised against **Asterisk only** — do your own acceptance test before relying on them
with FreeSWITCH:

- Registration failure paths (wrong credentials, `423 Interval Too Brief`)
- Single-leg in/outbound calls and the codec-negotiation matrix (PCMU / PCMA / G722)
- Blind transfer (REFER) as a separate scenario
- Session timers (RFC 4028)
- Early media (RFC 3960)
- TCP and TLS transport

See the [interop matrix](matrix.md) for the full picture.

## Directory user

A standard directory user (e.g. `conf/directory/default/1001.xml`) with a password works:

```xml
<user id="1001">
  <params>
    <param name="password" value="your-strong-password"/>
  </params>
  <variables>
    <variable name="user_context" value="default"/>
  </variables>
</user>
```

## Connect from the SDK

```csharp
var connect = await client.ConnectAsync(new SipAccount
{
    Username  = "1001",
    Password  = "your-strong-password",
    SipServer = "freeswitch.lan"    // the box running the internal SIP profile (5060)
});
```

## Expected configuration

- **Profile** — register against the `internal` profile unless you have a custom one.
- **Codecs** — set the profile's `inbound-codec-prefs` / `outbound-codec-prefs` to overlap
  with your `PreferredAudioCodecs` (`PCMU,PCMA` is the safe baseline; add `OPUS` if
  enabled).
- **DTMF** — RFC 4733 (`rfc2833`) is the default and matches the SDK.
- **SRTP** — set the profile/dialplan to SDES and use `SrtpPolicy.Required` to enforce it.

## Validate

Registration, a `default`-context dial to another extension, DTMF into an IVR, and SRTP
if configured. Reports welcome: [info@bechstein.digital](mailto:info@bechstein.digital).
