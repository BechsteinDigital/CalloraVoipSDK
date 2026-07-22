# NAT and SIP trunks

Getting media through NAT and registering against SIP trunks are the two most common
real-world hurdles. This guide covers what the SDK does automatically and what you must
configure.

## Choosing the SIP transport

By default the SDK sends outbound requests over UDP. Enterprise proxies that only accept TCP
or TLS can be served by setting the default transport once:

```csharp
using var client = new VoipClient(new VoipConfiguration
{
    DefaultTransport = SipTransport.Tls   // Udp (default) / Tcp / Tls / Ws / Wss
});
```

With `AddCalloraVoip(...)` use `options.DefaultTransport` or `builder.WithTransport(SipTransport.Tls)`.
A `sips:` scheme or an explicit `;transport=` on the target URI still overrides the default per call.

## Advertised media address

The SDK resolves the media address it advertises in SDP so it does not offer a loopback
or wrong-interface address to a LAN or WAN peer. In straightforward NAT setups where the
PBX/trunk performs the address fix-up (symmetric RTP / latching), this is usually enough
for two-way audio.

Behind CGNAT or a static 1:1 NAT where the peer does **not** latch to the source address, you can
force the public media IP into the SDP `c=` line:

```csharp
new SipAccount
{
    // ... credentials ...
    PublicSipHost   = "203.0.113.7",   // public signaling contact (Contact / Via)
    PublicMediaHost = "203.0.113.7"    // opt-in: public IP forced into SDP media (c=)
};
```

`PublicMediaHost` is an advanced opt-in: leave it unset (the default) to keep the auto-resolved,
symmetric-RTP-friendly address. Only set it when the RTP port is preserved end-to-end; a public
address with a remapped port breaks media. Non-IP values are ignored.

## ICE (opt-in, experimental)

Full ICE (RFC 8445 / RFC 7675) is implemented **opt-in** and is still marked unproven in
production. Enable it via `VoipConfiguration.Ice`. Treat it as experimental and validate
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

## Custom headers and caller identity

Add extra headers to an outbound INVITE for trunk/PBX routing (protected dialog/transport
headers and header-injection attempts are refused):

```csharp
await line.DialAsync("sip:4930999@trunk.provider.example", new DialOptions
{
    CustomHeaders = new Dictionary<string, string>
    {
        ["X-Trunk-Account"] = "acme-42"
    }
});
```

On inbound calls the peer-asserted identity and diversion history are read-only on the call:

```csharp
var caller    = call.RemoteAssertedIdentity;  // P-Asserted-Identity (RFC 3325), trusted peers only
var divertedFrom = call.Diversion;            // Diversion (RFC 5806), when present
```

## Reliable provisionals

The SDK sends reliable provisional responses (RFC 3262, `100rel`) only when the peer
explicitly requires them (`Require: 100rel`). This avoids interop friction with peers
that don't expect PRACK.

## Provider notes

Interop specifics per provider/PBX live under **Interop**:

- [FRITZ!Box](../interop/fritzbox.md) — verified **manually** against a live device (not an
  automated test); source of several hardening fixes
- [Asterisk](../interop/asterisk.md) — REGISTER covered by an **automated** CI interop test
  (full call/media not yet)
- [sipgate](../interop/sipgate.md), [FreeSWITCH](../interop/freeswitch.md),
  [3CX](../interop/3cx.md) — configuration guidance only; see the
  [matrix](../interop/matrix.md) for the full verification status

## Diagnostics

Enable the SIP wire trace (Trace log level) to see the actual SDP being offered/answered
when audio is one-way or absent — see [Diagnostics](../production/diagnostics.md).
