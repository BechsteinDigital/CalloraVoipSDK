# FreeSWITCH

**Status: 🧪 automated interop suite, run locally.** FreeSWITCH is the second PBX behind the shared
`IPbxFixture` abstraction: the same interop matrix that runs against Asterisk also runs against a
real FreeSWITCH container (`tests/CalloraVoipSdk.InteropTests`, trait `InteropFreeSwitch`).

The suite is **not yet part of the PR CI gate** — it is a local-first check, so regressions are
caught only when it is run explicitly:

```bash
dotnet test tests/CalloraVoipSdk.InteropTests -c Release --filter "Category=InteropFreeSwitch"
```

Run your own acceptance test for anything you depend on before production.

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
