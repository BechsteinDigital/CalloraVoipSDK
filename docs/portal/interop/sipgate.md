# sipgate

**Status: ⚙️ configuration guidance — not yet formally verified.** The SDK speaks standard
trunk SIP and is expected to register and call against sipgate, but we have not yet run a
validated end-to-end interop test. Run your own acceptance test before production use.

## Credentials

sipgate provides SIP credentials per line/trunk (a SIP user and password, plus a
registrar host). Use them directly:

```csharp
var connect = await client.ConnectAsync(new SipAccount
{
    Username       = "your-sipgate-sip-id",
    Password       = "your-sipgate-sip-password",
    SipServer      = "sipgate.de",          // use the registrar host sipgate gives you
    InboundNumbers = new[] { "4930xxxxxxx" }, // your DID(s) for inbound matching
    AcceptTrunkInbound = true
});
```

## Expected configuration

- **DIDs** — set your assigned numbers in `InboundNumbers` so inbound calls addressed to
  the DID are matched.
- **Codecs** — keep G.711 (PCMA/PCMU) in `PreferredAudioCodecs`; add Opus only if your
  account/route supports it.
- **NAT** — for cloud/hosted deployments behind NAT, plan for an SBC or symmetric-RTP
  path; ICE is opt-in and experimental (see [NAT and SIP trunks](../guides/nat-and-trunks.md)).

## Before you rely on it

Validate: registration refresh, inbound to your DID, outbound PSTN, DTMF into an IVR, and
call teardown. If it works (or doesn't), an interop report is welcome —
[info@bechstein.digital](mailto:info@bechstein.digital).
