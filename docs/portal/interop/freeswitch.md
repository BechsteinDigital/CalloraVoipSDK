# FreeSWITCH

**Status: ⚙️ configuration guidance — not yet formally verified.** The SDK is a standard SIP
endpoint and is expected to register against a FreeSWITCH SIP profile, but we have not yet
run a validated interop test. Run your own acceptance test first.

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
