# FRITZ!Box

**Status: ✅ verified against a live device (manual).** Registration, dialing, two-way audio and
DTMF were exercised by hand against a real AVM FRITZ!Box, and several SDK hardening fixes came
directly out of that test (advertised media-address resolution, static payload types
without `rtpmap`, codec preference). This is a manual verification against real hardware, not an
automated CI test.

## Set up an IP phone on the FRITZ!Box

1. In the FRITZ!Box UI: **Telephony → Telephony Devices → Configure New Device →
   Telephone (LAN/DECT)** and choose a **LAN/WLAN (IP telephone)**.
2. Note the **username** and **password** the FRITZ!Box assigns.
3. The **registrar** is the FRITZ!Box IP (commonly `fritz.box` / `192.168.178.1`).

## Connect from the SDK

```csharp
var connect = await client.ConnectAsync(new SipAccount
{
    Username  = "620",                 // the IP-phone user from the FRITZ!Box
    Password  = "your-device-password",
    SipServer = "fritz.box"            // or the box IP
});
```

Dial internal numbers (e.g. `**9` for a broadcast, or another registered device) or
external numbers per your FRITZ!Box dialing rules.

## Notes from the interop test

- **Codec** — FRITZ!Box favours G.711 (PCMA/PCMU). If you also enable Opus, keep G.711 in
  your `PreferredAudioCodecs` list as a fallback.
- **Media address** — the SDK advertises a routable LAN address (not loopback) so audio
  flows both ways on the local network. This was one of the fixes surfaced here.
- **DTMF** — RFC 4733 telephone-events work; verify tone detection on your target IVR.

## If audio is one-way

Enable the [SIP wire trace](../production/diagnostics.md) and check the `c=`/`m=` lines
in the offered/answered SDP against the actual device address. See
[NAT and SIP trunks](../guides/nat-and-trunks.md).
