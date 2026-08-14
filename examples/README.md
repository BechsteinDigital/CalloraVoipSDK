# CalloraVoipSdk — Examples

Runnable samples for the SDK. Each is a standalone project referencing the SDK
via `ProjectReference` and is part of `CalloraVoipSdk.sln`. All are console
applications except `WebRtcVideoCall.Web`, which is a web host.

## SIP

These need a real SIP server/PBX — point them at your own extension or trunk credentials.

| Sample | Shows | Docs |
|--------|-------|------|
| [BasicCalling](CalloraVoipSdk.Sample.BasicCalling) | Register, place/receive a call, default audio, interactive control | [Getting Started](../docs/portal/getting-started/outbound-call.md) |
| [Dialer](CalloraVoipSdk.Sample.Dialer) | Sequential campaign dialing over a list of targets | [Making calls](../docs/portal/guides/making-calls.md) |
| [Transfer](CalloraVoipSdk.Sample.Transfer) | Blind and attended transfer | [Making calls](../docs/portal/guides/making-calls.md) |
| [CustomAudio](CalloraVoipSdk.Sample.CustomAudio) | Media tap: receive frame stats + inject a generated PCMU tone (no audio hardware) | [Media tap](../docs/portal/guides/media-tap.md) |
| [Switchboard](CalloraVoipSdk.Sample.Switchboard) | Multiple concurrent inbound/outbound calls; connect two of them via attended transfer **or** a MediaConnector bridge | [Making calls](../docs/portal/guides/making-calls.md) · [Bridge](../docs/portal/guides/bridge-calls.md) |
| [VideoCalling](CalloraVoipSdk.Sample.VideoCalling) | `IVideoSender`/`IVideoReceiver` on a SIP call, and wiring `RecommendedBitrateChanged` straight into your encoder | [Video calls](../docs/portal/guides/video-calls.md) |

## WebRTC

Self-contained: both peers run in the process and signal over an in-memory channel, so
these need no PBX and no browser.

| Sample | Shows | Docs |
|--------|-------|------|
| [WebRtcPeer](CalloraVoipSdk.Sample.WebRtcPeer) | The public WebRTC facade end-to-end: two peers, SDK-driven offer/answer, a media tap on one side and `TrackReceived` on the other | [WebRTC](../docs/portal/guides/webrtc.md) |
| [WebRtcRecording](CalloraVoipSdk.Sample.WebRtcRecording) | Recording through an L3 media tap — every inbound audio payload captured to a buffer | [Recording](../docs/portal/guides/recording-playback.md) · [Media tap](../docs/portal/guides/media-tap.md) |
| [WebRtcDependencyInjection](CalloraVoipSdk.Sample.WebRtcDependencyInjection) | Two-facade composition in a container: `AddCalloraVoip(…)` alongside `AddWebRtc(…)`, client resolved from DI | [WebRTC](../docs/portal/guides/webrtc.md) |
| [WebRtcVideoCall.Web](CalloraVoipSdk.Sample.WebRtcVideoCall.Web) | A two-person browser video call: minimal WebSocket signalling relay plus a static page using native `RTCPeerConnection`. **The SDK peer is not in the media path here** — the browsers connect directly and this host only forwards SDP/ICE | [README](CalloraVoipSdk.Sample.WebRtcVideoCall.Web/README.md) |

> The SDK is **transport-only** for media: it moves finished codec bytes (VP8/H.264/Opus/…)
> and hands you a recommended bitrate, but never encodes or decodes. Camera, encoder and
> decoder belong to your application — the video samples mark that seam explicitly.

## Run

The samples multi-target `net8.0;net9.0;net10.0`. With `dotnet run` you must pass the
framework you have installed via `-f`:

```bash
dotnet run --project examples/CalloraVoipSdk.Sample.BasicCalling -f net9.0
dotnet run --project examples/CalloraVoipSdk.Sample.Switchboard   -f net9.0
# samples with arguments:
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.Dialer      -- <server> <user> <password> <target1> [target2 ...]
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.Transfer    -- <server> <user> <password> [target-A]
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.CustomAudio -- <server> <user> <password> <target>
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.VideoCalling -- <server> <user> <password> <target>

# WebRTC samples need no arguments and no PBX — both peers run in-process:
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.WebRtcPeer
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.WebRtcRecording
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.WebRtcDependencyInjection

# The browser sample is a web host — start it, then open the printed URL in two tabs:
dotnet run -f net9.0 --project examples/CalloraVoipSdk.Sample.WebRtcVideoCall.Web
```

The interactive samples (BasicCalling, Switchboard) default to **quiet logging**; pass
`-v` / `--verbose` to enable the SDK's debug/trace logs.

## Commercial examples

[`commercial/`](commercial) holds samples for the paid modules (Conference, Realtime,
WebSocket). They depend on modules that are **not** part of the open SDK core and are
therefore excluded from the solution build — see [commercial/README.md](commercial/README.md).
