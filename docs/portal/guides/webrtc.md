# WebRTC (preview)

> **Preview (v4.6.0-preview.1).** The WebRTC facade has not yet been validated against real browsers
> (Chrome/Firefox); its API may change before it is declared stable. Data channels (SCTP) and TURN relay
> are not included, and simulcast is send-side only (offerer-confirmed; receive-side RID demux is a later
> slice). Trickle ICE and early-bind have landed: an ephemeral media port yields a live m-line, and a
> fixed, reachable port is still recommended for NAT reachability without TURN.

The `CalloraVoipSdk.WebRtc` namespace is a signalling-neutral WebRTC peer surface that mirrors the
four-level design of `VoipClient`. It is **transport-only**: the SDK runs ICE, DTLS-SRTP, BUNDLE and
RTP/RTCP and moves already-encoded frames — your app owns the signalling channel and the codec.

## Create a peer and connect

Give the SDK your signalling channel (WebSocket, HTTP, Callora, …) by implementing `IWebRtcSignaling`;
the SDK drives the RFC 8829 offer/answer and completes when connected:

```csharp
using CalloraVoipSdk.WebRtc;

var rtc = new WebRtcClient(new WebRtcConfiguration
{
    LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 46000),   // a reachable media port
    EnableVideo = true,
});

await using var peer = rtc.CreatePeer();

// Subscribe BEFORE connecting so inbound tracks are not missed.
peer.TrackReceived += (_, track) =>
    track.FrameReceived += (_, frame) => { /* your depacketised codec bytes */ };

await peer.ConnectAsync(mySignalling, WebRtcRole.Offerer);   // or WebRtcRole.Answerer
await peer.SendAudioAsync(encodedOpusPayload);
```

Prefer full control? Drive it yourself with the neutral primitives: `CreateOffer()`,
`SetRemoteDescriptionAsync(sdp)`, `StartAsync()`.

### Trickle ICE

Implement `IWebRtcTrickleSignaling` (it extends `IWebRtcSignaling`) instead of the plain interface and
`ConnectAsync` trickles candidates automatically: local candidates (host + server-reflexive from
configured STUN) are signalled as they are gathered, and remote candidates are applied during
negotiation. Driving signalling yourself? Subscribe to `LocalIceCandidateDiscovered`, call
`GatherCandidatesAsync()` for server-reflexive candidates, and feed the peer's candidates in with
`AddIceCandidateAsync(candidate)`.

## Tracks (the W3C model)

`TrackReceived` fires once per inbound track with a `RemoteTrack` (`Kind`, `StreamId` = the remote
`a=msid`, `TrackId`). Group by `StreamId` to keep one participant's audio and video together (e.g. for a
recording); subscribe per track to keep them separable (e.g. routing audio to a voice bot). Frames arrive
as `EncodedFrame` (payload, RTP timestamp, key-frame flag).

## Media taps (recording / analytics / AI)

Attach an `IMediaTap` to observe media in both directions without owning the peer:

```csharp
using var recording = peer.AttachMediaTap(new MyRecorder());   // OnAudio/OnVideo, Inbound + Outbound
```

## Dependency injection & composition

```csharp
services
    .AddCalloraVoip(voip => { /* SIP facade */ })
    .AddWebRtc(rtc => { rtc.EnableVideo = true; });   // WebRTC facade, composed in one chain
```

`IWebRtcClient.Peers` tracks live peers; `IWebRtcClient.Modules` registers facade plugins
(programmatically or auto-attached from DI as `IWebRtcClientModule` services).

## Screen sharing

Screen sharing needs no separate API: it is just video. Capture the screen with your platform's
API, encode the frames with your codec (transport-only — the SDK never encodes), and send them on the
peer's video track exactly like camera frames:

```csharp
// EnableVideo on the client, then feed screen-captured encoded frames instead of camera frames.
await peer.SendVideoFrameAsync(encodedScreenFrame, rtpTimestamp);
```

Screen content differs from camera content (higher resolution, lower frame rate, "detail" over
"motion") — size your encoder accordingly; the SDK moves the bytes unchanged. Sharing the screen
*alongside* the camera (two simultaneous video tracks) needs multi-video-track support, which is a
later slice — today one video track is negotiated per peer. A future `a=content` (RFC 4796) hint to
flag the track as screen content is optional and not yet emitted.

## Simulcast (send-side)

Send the same video at several resolutions/bitrates so an SFU can forward the layer each viewer can
afford (RFC 8853). Configure the layer rids on the client, encode each layer yourself (transport-only),
and send it on its rid:

```csharp
// Configure simulcast rids on the WebRtcConfiguration, then per frame, per layer:
await peer.SendVideoFrameAsync("hi", encodedHiRes, rtpTimestamp);
await peer.SendVideoFrameAsync("lo", encodedLoRes, rtpTimestamp);
```

The offer advertises the rids; the SDK confirms them against the answer and falls back to a single
stream when the answerer does not confirm them. The active `rid` is surfaced to `IMediaTap.OnVideo` and
to recording (RFC 8852). Receiving and demultiplexing a remote peer's simulcast layers by rid is a
later slice — today simulcast is send-side only.

## Samples

- `examples/CalloraVoipSdk.Sample.WebRtcPeer` — two peers connect over an in-memory channel, tracks + tap.
- `examples/CalloraVoipSdk.Sample.WebRtcRecording` — record inbound audio via a media tap.
- `examples/CalloraVoipSdk.Sample.WebRtcDependencyInjection` — DI + two-facade composition.
- `examples/CalloraVoipSdk.Sample.WebRtcVideoCall.Web` — a browser video-call website in its simplest
  form (WebSocket signalling relay + native browser WebRTC, two tabs = two people, peer-to-peer). The
  SDK media peer is not in the media path here — that is the browser-interop milestone.
