# NAT and SIP trunks

Getting media through NAT and registering against SIP trunks are the two most common
real-world hurdles. This guide covers what the SDK does automatically and what you must
configure.

## Advertised media address

The SDK resolves the media address it advertises in SDP so it does not offer a loopback
or wrong-interface address to a LAN or WAN peer. In straightforward NAT setups where the
PBX/trunk performs the address fix-up (symmetric RTP / latching), this is usually enough
for two-way audio.

## ICE (opt-in, experimental)

Full ICE (RFC 8445 / RFC 7675) is implemented **opt-in** and is still marked unproven in
production. Enable it via `SdkConfiguration.Ice`. Treat it as experimental and validate
against your own network before relying on it. Without ICE, plan for a
media-relaying/SBC path on hostile NAT.

## Registering against a trunk

```csharp
var connect = await client.ConnectAsync(new SipAccount
{
    Username           = "trunkuser",
    Password           = "secret",
    SipServer          = "trunk.provider.example",
    RegistrationExpiry = 300,
    InboundNumbers     = new[] { "4930123456" },  // DIDs you own
    AcceptTrunkInbound = true
});
```

`InboundNumbers` lets the SDK match inbound requests addressed to your DIDs rather than a
registered extension AOR.

## Reliable provisionals

The SDK sends reliable provisional responses (RFC 3262, `100rel`) only when the peer
explicitly requires them (`Require: 100rel`). This avoids interop friction with peers
that don't expect PRACK.

## Provider notes

Interop specifics per provider/PBX live under **Interop**:

- [FRITZ!Box](../interop/fritzbox.md) — the one platform validated in a real interop test
- [sipgate](../interop/sipgate.md), [Asterisk](../interop/asterisk.md),
  [FreeSWITCH](../interop/freeswitch.md), [3CX](../interop/3cx.md) — configuration
  guidance; see the [matrix](../interop/matrix.md) for verification status

## Diagnostics

Enable the SIP wire trace (Trace log level) to see the actual SDP being offered/answered
when audio is one-way or absent — see [Diagnostics](../production/diagnostics.md).
