# WebRTC

> **Status (4.11).** The WebRTC facade is **validated in CI against real browsers** — Chromium and
> Firefox, headless via Playwright, audio and VP8 video, in both roles (SDK as offerer and as
> answerer), over DTLS-SRTP including AES-GCM. Known scope limits: data channels (SCTP) are **not
> included**; the **TCP/TLS TURN relay** (new in 4.11) is available but unit-proven only — its
> browser/real-server data-path validation is tracked in the interop matrix, while the **UDP relay** is
> production-proven; and Safari/WebKit is not yet verified. The media socket follows the address family of the configured `LocalEndPoint`, so IPv4
> and IPv6 both work. Browser **mDNS (`.local`) host candidates are resolved** via the OS resolver
> (RFC 8828). Trickle ICE and early-bind are included: an ephemeral media port still yields a live
> m-line, though a fixed, reachable port remains the recommendation for NAT reachability without
> TURN.

> **Multi-party / SFU enablement (4.7, stable).** Additive, transport-only primitives on top of the
> 4.6 facade: **multiple video and audio tracks** over one BUNDLE, **mid-call renegotiation**
> (RFC 8829), **receive-side simulcast demux** (the RID a layer arrived on is surfaced per frame),
> and a **per-peer recommended outgoing bitrate** derived from transport-cc feedback. All additive —
> a peer that uses none of them negotiates byte-identical SDP and behaves exactly as before. The SDK
> stays a peer, not a conference server: it **forwards, it does not mix or transcode**. One limit
> worth knowing up front: these primitives are **not yet covered by the browser-interop CI matrix** — the
> browser suite exercises the 1 audio + 1 video path. (ICE restart on a connected peer is supported since
> 4.11 via `CreateIceRestartOfferAsync`.) See the sections below.

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

### Negotiation correctness (4.8)

- A structurally non-conforming remote answer is **rejected** (RFC 3264 §6 / RFC 8829): m-line
  count/order, 1:1 MIDs, transport profile, PT subset and BUNDLE-group subset are validated, and the
  offerer fails closed (`State → Failed`) instead of proceeding on a mismatched answer.
- The offerer sends the codec the **answer accepted** (RFC 3264 §6.1), not always its own first offered
  codec; with no common codec it logs a warning and fails closed.
- Inbound audio now carries its real RTP timestamp (`EncodedFrame.RtpTimestamp`, RFC 3550 §5.1) — an
  SFU forwarding received audio no longer stamps it at `0`.
- A peer DTLS `close_notify`/alert ends the association and surfaces as
  `State == PeerConnectionState.Closed` (`ConnectionStateChanged`, RFC 8827 §6.5) — media does not keep
  flowing under a keying channel the peer has closed.

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
"motion") — size your encoder accordingly; the SDK moves the bytes unchanged.

Sharing the screen *alongside* the camera is supported since 4.7 — add a second video track and send
each on its own handle, so the two stay separable on the wire with distinct SSRCs:

```csharp
IVideoTrack screen = peer.AddVideoTrack();
await peer.SendVideoFrameAsync(cameraFrame, ts);        // primary track
await screen.SendFrameAsync(screenFrame, ts);           // the added track
```

See [Multiple video tracks](#multiple-video-tracks-and-mid-call-renegotiation). A future `a=content`
(RFC 4796) hint to flag the track as screen content is optional and not yet emitted, so the receiver
tells them apart by MID / `a=msid`, not by a content tag.

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
to recording (RFC 8852). Receiving a remote peer's simulcast layers is covered by
[Receive-side simulcast demux](#receive-side-simulcast-demux) below.

## Multiple video tracks and mid-call renegotiation

> **4.7, additive, transport-only.**

`AddVideoTrack()` adds a further video track — its own `m=video` line and SSRC on the shared BUNDLE
transport — and returns an `IVideoTrack` to send on. Call it **before or after** connect:

```csharp
IVideoTrack camera = peer.AddVideoTrack();
IVideoTrack screen = peer.AddVideoTrack(new VideoTrackOptions { /* direction, codecs, simulcast, stream id */ });
await camera.SendFrameAsync(frame, rtpTimestamp);

// Inbound video tracks are told apart by mid:
peer.TrackReceived += (_, track) => { var mid = track.Mid; };

// Refresh exactly one track's decoder (RFC 4585 §6.3.1 PLI):
await peer.RequestVideoKeyFrameAsync(mid);
```

Adding a track on a **connected** peer applies live: a second `CreateOffer` /
`SetRemoteDescriptionAsync` cycle applies the delta with **no transport, DTLS, ICE or SRTP rebuild**,
and the tracks already running keep flowing. A new track's SSRCs are allocated distinct from every live
one, so it never collides a running stream's per-SSRC SRTP context (RFC 3550 §8.1). Observe where you
are in the exchange via `SignalingState` / `SignalingStateChanged` (W3C `RTCSignalingState`).

**ICE restart.** A re-offer that rotates the ICE ufrag on the shared transport re-gathers and re-nominates
connectivity in place (`CreateIceRestartOfferAsync`, since 4.11) — the DTLS association and SRTP contexts are
preserved, so the peer need not be disposed and rebuilt. A peer that uses only
`WebRtcConfiguration.EnableVideo` and never calls `AddVideoTrack` emits the byte-identical 1+1 SDP as
before, and `SendVideoFrameAsync` still addresses the primary track.

## Multiple audio tracks

> **4.7, additive, transport-only.**

Add more than one audio track on the shared BUNDLE transport — one `m=audio` line, SSRC and
per-participant `a=msid` per track — so an SFU can forward several participants' audio to a peer
without a second connection:

```csharp
// The primary audio track anchors ICE/DTLS and is created with the peer.
IAudioTrack extra = peer.AddAudioTrack();                        // or AddAudioTrack(new AudioTrackOptions { ... })
await extra.SendFrameAsync(encodedOpusPayload, rtpTimestamp);    // track.Mid / track.Direction describe it

// Inbound audio tracks arrive tagged with their mid:
peer.TrackReceived += (_, track) => { var mid = track.Mid; /* separate per participant */ };
```

Tracks add and remove mid-call through the renegotiation path. The added send path threads your RTP
timestamp through unchanged, so A/V sync holds against the same participant's forwarded video.

**Honest limits.** The **primary** audio m-line anchors ICE/DTLS and is never deactivated — a peer
that uses only the one audio track produces byte-identical SDP to before. DTMF (RFC 4733) stays on
the primary track, not per-MID. This is the forwarding building block for multi-party audio; the SDK
**forwards, it does not mix** the conference.

## Receive-side simulcast demux

> **4.7, additive, forwarding-only.**

When a remote peer sends several encodings of **one** video m-line, the SDK separates them
receive-side into independent per-RID reassembly (each layer keeps its own reorder + depacketise
state) and tags every frame with the RID it arrived on. There is still **one** `RemoteTrack` per
m-line; distinguish the layers by `frame.Rid`:

```csharp
peer.TrackReceived += (_, track) =>
    track.FrameReceived += (_, frame) =>
    {
        string? rid = frame.Rid;   // the a=rid layer id, e.g. "hi" / "lo"; null when not simulcast
        // forward or select by rid — the SFU decides which layer goes where
    };
```

This completes simulcast (the send side was already there): an SFU receives each layer addressably.
**Forwarding-only** — the SDK never drops or transcodes a layer; which layer is forwarded is your
SFU/app logic. Non-simulcast receive is byte-identical (`Rid` is `null`).

## Per-peer bitrate recommendation

> **4.7, additive.**

A finished recommended send bitrate toward the connected peer — plus a coarse `NetworkQuality` —
derived from the transport-wide congestion feedback the peer returns (transport-cc,
draft-holmer-rmcat-transport-wide-cc-extensions-01 — RTPFB FMT=15, the format Chrome and libwebrtc use.
RFC 8888 CCFB is a different message, FMT=11, and is not implemented). For an
SFU this is the per-receiver signal of which simulcast layer to forward:

```csharp
peer.RecommendedBitrateChanged += (_, recommendation) =>
{
    long bps = recommendation.BitrateBps;         // recommendation.Quality is a coarse NetworkQuality
    // choose the layer / pace your encoder — the SDK does not decide for you
};

long? bps = peer.RecommendedOutgoingBitrateBps;   // null until transport-cc is negotiated
```

**A recommendation, not raw metrics, and reactive** — it fires per feedback interval, no polling. The
property and event stay `null`/silent when transport-cc was not negotiated. The SDK does **no**
throttling (your app owns the cadence) and makes **no** layer decision.

## SFU enablement — how these fit together

These primitives are the peer-side building blocks for multi-party conferencing: multiple video and
audio tracks carry several participants over one transport, mid-call renegotiation lets participants
join and leave without rebuilding the connection, receive-side simulcast makes each video layer
addressable, and the per-peer bitrate recommendation tells the forwarder which layer each receiver can
afford. The SDK supplies the peer primitives; the **SFU / selection logic lives in your app or
conference host** — the SDK is not itself an SFU, and it never mixes or transcodes.

They are also **not yet covered by the browser-interop CI matrix**, which exercises the 1 audio +
1 video path against Chromium and Firefox. Validate a multi-track topology against your own clients
before relying on it — see the [browsers page](../interop/browsers.md).

## Samples

- `examples/CalloraVoipSdk.Sample.WebRtcPeer` — two peers connect over an in-memory channel, tracks + tap.
- `examples/CalloraVoipSdk.Sample.WebRtcRecording` — record inbound audio via a media tap.
- `examples/CalloraVoipSdk.Sample.WebRtcDependencyInjection` — DI + two-facade composition.
- `examples/CalloraVoipSdk.Sample.WebRtcVideoCall.Web` — a browser video-call website in its simplest
  form (WebSocket signalling relay + native browser WebRTC, two tabs = two people, peer-to-peer). The
  SDK media peer is not in the media path here — that is the browser-interop milestone.
