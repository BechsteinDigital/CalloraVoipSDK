# Progressive API

CalloraVoipSdk exposes one stack at several integration depths. You can begin with a
managed call workflow and move into typed call control, encoded media or extension
contracts only where your product needs them. The deeper contracts remain public and
supported; they do not require access to SIP/RTP implementation classes.

## Choose the shallowest useful depth

| Depth | Start here when | Main contracts |
|---|---|---|
| Managed workflows | The SDK should own the standard registration, dialing, device, playback or recording flow | `VoipClient`, `ConnectAsync`, `DialAndWaitUntilConnectedAsync`, `AttachDefaultAudioAsync`, recording/playback facades |
| Typed call control | Your application reacts to dialog state, controls an individual call or needs negotiated/quality state | `IPhoneLine`, `ICall`, `DialOptions`, call events and action results |
| Media and extension seams | Your product consumes/injects frames, bridges calls or replaces an SDK edge | `IMediaReceiver`, `IMediaSender`, `MediaConnector`, `IAudioDevice`, `ISipTelemetrySink`, `IVoipClientModule`, `ModuleRegistry` |

These depths compose. A dialer can use `DialAndWaitUntilConnectedAsync`, attach an
`IMediaReceiver` to one connected call for analysis, and still use the default audio
device on another call.

## 1. Managed workflows

The high-level surface owns the common orchestration and returns typed results:

```csharp
var connect = await client.ConnectAsync(account);
if (!connect.IsSuccess || connect.Line is null)
    throw new InvalidOperationException($"Registration failed: {connect.Status}");

var dial = await client.DialAndWaitUntilConnectedAsync(
    connect.Line,
    "sip:1002@pbx.example.com");

if (!dial.IsSuccess || dial.Call is null)
    throw new InvalidOperationException($"Dial failed: {dial.Status}");

await client.AttachDefaultAudioAsync(dial.Call);
```

Use this depth for ordinary registration, inbound/outbound calls, OS audio, playback and
recording. It minimizes application-owned state machines without hiding the resulting
`IPhoneLine` or `ICall`.

## 2. Typed call control

The returned domain objects remain available for product-specific control:

- `IPhoneLine` exposes registration state, inbound calls, early outbound ringing,
  reconnect state, messaging, publication and explicit unregistration.
- `ICall` exposes accept/reject, hangup, hold/unhold, DTMF, blind/attended transfer,
  redirect and in-dialog `INFO`, `OPTIONS`, `SUBSCRIBE` and `NOTIFY`.
- `DialOptions` controls ring timeout, display name, outbound proxy, per-call SRTP and
  injection-guarded outbound INVITE headers.
- `ICall.MediaParameters`, `EarlyMediaSdp`, `RtpStatistics`, `QualitySnapshot` and ICE
  state expose negotiated and measured call state without exposing parser internals.
- Call, hold, DTMF, transfer, quality and ICE events allow live orchestration.

This is the right depth for campaign logic, contact-center routing, protocol-aware
workflows and diagnostics. Event callbacks run synchronously on signaling or media
threads as documented in [Events](events.md); handlers must return quickly and off-load
blocking or I/O work.

## 3. Media and extension seams

For media-aware products, the public tap moves encoded payloads in and out of a live
call:

```csharp
using IMediaReceiver receiver = client.Media.CreateReceiver();
using IMediaSender sender = client.Media.CreateSender();

receiver.AttachToCall(call);
sender.AttachToCall(call);
```

`IMediaReceiver` delivers the negotiated encoded frames; `IMediaSender` injects frames
already encoded for the call. `MediaConnector` cross-connects calls. These contracts
support bots, STT/TTS adapters, recording pipelines and custom routing without reaching
into `Infrastructure`.

The same extension depth includes:

- custom `IAudioDevice` registration through `CalloraBuilder.WithAudioDevice<T>()`
- custom `ISipTelemetrySink` registration through
  `CalloraBuilder.WithTelemetrySink<T>()`
- separately shipped `IVoipClientModule` implementations discoverable through
  `client.Modules`

### Media hot-path contract

`IMediaReceiver.FrameReceived` is synchronous on the RTP media path. A handler must not
block or perform I/O inline; it should enqueue into its own bounded pipeline and return.
The SDK deliberately does not add buffering or backpressure to this tap. Frames are
encoded codec payloads, not PCM, and consumers own decode/encode work. See
[Media tap](../guides/media-tap.md) and
[ADR-059](../../adr/ADR-059-public-media-tap-contract.md).

## Intentional boundary

The progressive API is not arbitrary access to every SIP packet or internal state
machine. Transport implementations, parsers, RTP sessions and other
`Core.Infrastructure` details remain internal so they can evolve without turning every
internal refactor into a consumer breaking change.

Known examples of the boundary:

- outbound custom INVITE headers are supported through `DialOptions.CustomHeaders`;
  custom inbound INVITE headers are not currently exposed on `ICall`
- the media tap exposes negotiated encoded payloads, not raw encrypted SRTP packets
- custom audio devices, telemetry sinks and modules are supported seams; replacing an
  internal dialog parser is not

If a requirement falls beyond these seams, it is a core contribution or a new public
contract decision, not an invitation to depend on implementation types.

## What is proven today

The Asterisk interop suite exercises both convenience and deeper public contracts:
registration and dialing, direct `ICall` lifecycle/control, negotiated media state,
per-call receiver/sender access, media bridging and a two-leg PBX call with byte-exact
bidirectional media through the public media API.

That proves the layers compose on the tested SIP/RTP path. It does **not** prove every
extension seam against every competing SDK or peer. Custom devices, telemetry, modules,
all in-dialog actions, ICE/WebRTC and arbitrary network conditions have their own test
evidence and caveats; consult the [interop matrix](../interop/matrix.md) and capability
documentation before making a production claim.

## Continue reading

- [VoipClient](voipclient.md) — lifecycle, sub-facades and convenience methods
- [Calls](calls.md) — typed control and event surface
- [Media](media.md) — receivers, senders, connectors and devices
- [Modules](modules.md) — separately shipped extension packages
- [RTCP quality metrics](../guides/rtcp-quality-metrics.md)
- [SRTP/SRTCP](../guides/srtp-srtcp.md)
- [WebRTC](../guides/webrtc.md)
