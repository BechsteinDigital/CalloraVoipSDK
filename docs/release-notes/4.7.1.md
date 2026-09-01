# CalloraVoipSdk 4.7.1

**WebRTC/SFU correctness patch for the 4.7 line.** 4.7.1 hardens browser-facing multi-party negotiation and
media routing while retaining the additive 4.7 surface: several video *and* audio tracks over one BUNDLE
transport, mid-call renegotiation, receive-side simulcast, and per-peer bitrate recommendations.

Everything is **additive and transport-only** — a peer that uses none of it negotiates byte-identical SDP and
behaves exactly as in 4.6. The SDK stays a *peer*: it forwards media, it does not mix or transcode, and the
SFU / selection logic lives in your app or conference host.

## Fixed in 4.7.1

- **Stable browser-safe MIDs:** opt-in numeric media IDs preserve already-negotiated m-line identity and append
  runtime audio/video tracks in insertion order during RFC 8829 renegotiation.
- **Outbound additional audio:** a local `sendonly` audio track accepted as `recvonly` by the browser now gets a
  live bundle sender instead of failing with “no additional audio track with MID”.
- **ICE pair progression:** a lower-priority reachable candidate is checked before an unreachable higher-priority
  candidate consumes another retry round.
- **Configuration parity:** `UseStableNumericMediaIds` flows through configuration, options, mappings, and the
  public API baseline.

## Highlights

- **Multiple video tracks + mid-call renegotiation (RFC 8829)** — add a video track before *or* after connect.
- **Multiple audio tracks over one BUNDLE** — forward several participants' audio without a second connection.
- **Receive-side simulcast demux (RFC 8853/8852)** — each inbound layer arrives addressable, tagged by RID.
- **Per-peer send-bitrate recommendation (transport-cc, RFC 8888)** — a finished number, reactive per feedback.
- **Public recording-encryption factory** — build the shipped AES-256-GCM provider without your own crypto.

## What's new

### Multiple video tracks + mid-call renegotiation (RFC 8829)
`IPeerConnection.AddVideoTrack()` (and the `AddVideoTrack(VideoTrackOptions)` overload) adds a further video
track — its own `m=video` line, SSRC and `RemoteTrack.Mid` — **before or after connect**. A second
`CreateOffer` / `SetRemoteDescriptionAsync` cycle applies the delta to the running session **live**: no
transport / DTLS / ICE / SRTP rebuild, existing tracks keep flowing. `IPeerConnection.SignalingState` and the
`SignalingStateChanged` event surface the RFC 8829 state, and `RequestVideoKeyFrameAsync(mid)` refreshes one
specific track. New public types: `IVideoTrack`, `VideoTrackOptions`, `TrackDirection`, `SignalingState`.

> ICE restart is **not** supported — a re-offer that rotates the ICE ufrag is rejected; dispose and re-create
> the peer to restart ICE.

### Multiple audio tracks over one BUNDLE
`IPeerConnection.AddAudioTrack()` returns an `IAudioTrack`; each track carries its own `m=audio` line, SSRC and
per-participant `a=msid`, received per track (`RemoteTrack.Mid`), added/removed mid-call via renegotiation. The
added-audio send path threads your RTP timestamp through to the wire, so a forwarding SFU keeps A/V sync
against the same participant's video. The **primary** audio m-line anchors ICE/DTLS and is never deactivated
(single-track SDP is byte-identical); DTMF stays on the primary track. New public types: `IAudioTrack`,
`AudioTrackOptions`.

### Receive-side simulcast demux (RFC 8853/8852)
When a peer sends several encodings of one video m-line, the SDK separates them receive-side into independent
per-RID reassembly and tags every frame with its layer id on the new **`EncodedFrame.Rid`** — one `RemoteTrack`
per m-line, layers told apart by `frame.Rid`. This completes simulcast (the send side already shipped).
Forwarding-only: no layer is dropped or transcoded; which layer to forward is your SFU logic. Non-simulcast
receive is byte-identical (`Rid` is `null`).

### Per-peer send-bitrate recommendation (transport-cc, RFC 8888)
`IPeerConnection.RecommendedOutgoingBitrateBps` and the `RecommendedBitrateChanged` event give a **finished
recommended send bitrate** (plus a coarse `NetworkQuality`) toward each connected peer, derived from the
transport-wide congestion feedback that peer returns — the signal an SFU uses to pick which simulcast layer to
forward. A recommendation, not raw metrics, and reactive (fires per feedback interval, no polling). Property
and event are `null`/silent when transport-cc was not negotiated; the SDK does no throttling and makes no layer
decision. New public type: `BitrateRecommendation`.

### Recording encryption factory
`CalloraVoipSdk.Hosting.RecordingEncryption.FromKey(key)` / `FromPassphrase(passphrase, salt, iterations)`
build the built-in AES-256-GCM `IRecordingEncryptionProvider` ready to assign to
`RecordingOptions.EncryptionProvider` — restoring construction access after the concrete provider became
internal earlier in this line.

## Upgrading from 4.6

4.7 is a minor, additive release; existing single-audio + single-video code is unchanged. Two namespace moves
require a `using` update at the call site:

- **SIP telemetry contract** → `CalloraVoipSdk.Core.Application.Observability` (`ISipTelemetrySink`,
  `SipEventRecord`, `SipMetricRecord`, `SipCdrRecord`).
- **`TlsConfiguration`** → `CalloraVoipSdk.Core.Application.Ports.Security` (now a pure DTO; the certificate
  behaviour moved into the SIP transport and is no longer public).

Two accidentally-public types are now `internal` — use the public seam instead:

- `AesGcmRecordingEncryptionProvider` → build it via the new `Hosting.RecordingEncryption` factory
  (`IRecordingEncryptionProvider` stays the public contract).
- `SipDomainCertificateValidator` (internal RFC 5922 helper).

The type shapes and the `VoipConfiguration.Tls` / `VoipOptions.Tls` flow are otherwise unchanged.

## Known limitations

- **Multi-track claim-gating:** the offerer-driven mid-call track add is covered end-to-end; broader
  renegotiation test coverage (answerer-driven add, deactivate-then-add in one cycle, direction toggle on a
  live track, renegotiation racing teardown) is still being broadened before the unqualified "multi-track
  done" claim.
- The new N-audio and receive-side-simulcast paths are **not yet in the browser-interop CI matrix** (the base
  WebRTC facade remains validated against real Chromium and Firefox).
- Unchanged scope limits: no SCTP **data channels**, TURN relay is **UDP-only** (no TCP/TLS relay), and **full
  ICE** (RFC 8445/7675) is opt-in and not yet production-proven.

## Install

```bash
dotnet add package CalloraVoipSdk.Core --version 4.7.1
dotnet add package CalloraVoipSdk.Client --version 4.7.1   # the WebRtc facade (CalloraVoipSdk.WebRtc)
```

Full detail in [`CHANGELOG.md`](CHANGELOG.md).
