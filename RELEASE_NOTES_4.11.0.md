# CalloraVoipSdk 4.11.0

**Media over a TCP/TLS TURN relay, and a WebRTC peer that survives an ICE restart.** Until now the TURN relay
data path was **UDP-only**: a caller on a network that blocks UDP and allows only outbound 443 had no
last-resort path, and a WebRTC peer whose connectivity broke (a network change, a NAT rebind) had to be
disposed and rebuilt. 4.11.0 closes both — the two "deliberate limits" the 4.10 line called out.

## Highlights

### Media over a TCP/TLS TURN relay (#240, ADR-073)

The relay data path is no longer UDP-only. A relay ICE candidate can now carry media over a **persistent
TCP/TLS connection** to the TURN server (RFC 8656 §2.1 client-server transport, ChannelData framing §12/§12.5).
It is a first-class ICE candidate, not a bespoke path: it is gathered alongside host/srflx/UDP-relay,
direct-preferred by pair priority, and once a stream relay pair wins ICE the session's media (DTLS, RTP/RTCP)
flows as ChannelData over the stream — with **DTLS/SRTP riding above the transport unchanged**.

This is the model libwebrtc and pjnath use: media rides the transport the *winning candidate* selects (a TURN
allocation over its own socket), rather than a single shared socket switching modes. The existing UDP relay
keeps its in-place whole-socket switch; the stream relay is a distinct transport chosen by nomination.

Turn it on by adding a TURN server with a stream transport to your ICE servers:

```csharp
services.AddCalloraWebRtc()
    .WithTurnServer("turn.example.com", "user", "secret",
        port: 5349, transport: IceTransport.Tls);   // or IceTransport.Tcp (port defaults to 3478)
```

**TLS** gives a relay that traverses firewalls allowing only outbound 443 — the last-resort connectivity TURN
exists for, over the transport the most restrictive networks permit.

### A WebRTC peer survives an ICE restart (#226, ADR-072)

`IPeerConnection.CreateIceRestartOfferAsync(...)` produces a re-offer that **re-gathers and re-nominates
connectivity on the live peer** — the DTLS association and every SRTP context are preserved. Previously a peer
whose path died had to be torn down and rebuilt; now a network change is recovered in place.

### An offer can ask the peer to simulcast (#317, RFC 8853 §5.3)

The receive path has demultiplexed incoming simulcast layers since 4.9.0, but only a peer that *offered* could
reach it. `WebRtcConfiguration.SimulcastRecvLayers` (and per-track `VideoTrackOptions.SimulcastRecvRids`) let a
receive-only offerer — a conference host — ask the peer to send layers; `IPeerConnection.NegotiatedReceiveSimulcastRids`
reads back the set the answer confirmed.

## Also in 4.11.0

- **Media-flow / silence monitoring on `ICall`** (#261). A connected call is no longer torn down on media
  silence; instead `ICall.MediaFlowChanged` surfaces inbound-flow transitions, paced by
  `VoipConfiguration.MediaSilenceNotifyAfter`.
- **RTP header-extension coverage** — RFC 8285 two-byte header extensions (#224) and the AV1 **Dependency
  Descriptor** for key-frame and layer information (#225), plus `EncodedFrame.KeyFrameSource` / `SpatialId` /
  `TemporalId`.
- **SIP hardening** — RFC 3261 §19.1.4 URI comparison corrected across all ten worked examples (#285), and
  served-user gating (`SipSignalingHardeningConfiguration.ServedUserAors`, §8.2.2.1).
- **Static-IP trunks** — `SipAccount.Register` (default `true`; `false` for IP-authenticated trunks),
  `LineState.Ready`, `SipAccount.Username` no longer required, and `VoipConfiguration.LocalSipPort` /
  `LocalSipTlsPort`.
- **Video** — opaque (end-to-end encrypted) frame format via `WebRtcConfiguration.OpaqueVideoFrames` (#223),
  and per-stream key-frame requests (`IPeerConnection.VideoTrackKeyFrameRequested`, #227).

## Compatibility

- **Purely additive.** No public API was removed or its signature changed — verified by the tracked API
  baseline (`tests/CalloraVoipSdk.ArchitectureTests/PublicApi.approved.txt`). Adding members to public interfaces
  (`IPeerConnection`, `ICall`) is treated as additive per ADR-006 §2; a consumer that does not call the new
  members is unaffected.
- **Targets unchanged** — `net8.0`, `net9.0`, `net10.0`.
- **Open edges** carried in ADR-073 / the issue tracker: the stream relay's working path is the controlling
  (offerer) agent; the answerer-side stream relay and the real-server/browser data-path proof belong to the
  interop matrix (#228), and TLS certificate validation is currently the platform default (config plumbing is a
  follow-up).

See [`CHANGELOG.md`](CHANGELOG.md) for the concise, itemised entry.
