# ADR-059: Public Per-Call Media-Tap Contract (Synchronous, Encoded, Fan-Out)

Status: Accepted
Date: 2026-07-07

## Context

The plugin/module extension point (`IVoipClientModule` / `ModuleRegistry`, slice A2) gives external
packages a discovery seam, but a module such as a realtime-AI bridge (Callora.Realtime) needs a
second thing: a supported public way to observe inbound and inject outbound media on a live call
without reaching into `Infrastructure/*`. Slice A3 (CORE-301, "per-call frame access/injection")
built that contract as the first real consumer surface for the module system.

The strategic constraints were already fixed by earlier decisions:

- Codec handling is **transport-only** — the SDK does not natively encode/decode audio in the
  general media path (video-interop/codec decision).
- The media hot path must stay RAM-efficient and allocation-lean, with events firing synchronously
  on the SDK media/RTCP threads and handlers forbidden to block or throw (project hard rules 2/4,
  threading contract K3).
- ADR-008 (community module store) later *names* this tap (`CreateReceiver` / `CreateSender`) as one
  of the public contracts a third-party module may use, but it does not decide the tap's shape,
  delivery model, or frame representation. That design is what A3 settled and what this ADR records.

### Verified current state

- `IMediaReceiver` (`src/Core/Application/Media/IMediaReceiver.cs`): `event FrameReceived`,
  `AttachToCall(ICall)`, `Detach()`, `IDisposable`. XML doc states the event "runs synchronously on
  the media path; handlers must not block and must not perform I/O inline."
- `IMediaSender` (`src/Core/Application/Media/IMediaSender.cs`): `AttachToCall(ICall)`, `Detach()`,
  `SendAsync(MediaFrame, ct)`, `IDisposable`. Frames sent outside `Connected`/`OnHold` are dropped.
- `MediaFrame` (`src/Core/Application/Media/MediaFrame.cs`): a `readonly record struct` of
  `(ReadOnlyMemory<byte> Payload, int PayloadType, uint DurationRtpUnits)` — the payload is the
  **encoded** codec payload (PCMU/PCMA/G.722…), explicitly "not decoded PCM".
- Both live in `Core.Application.Media` (layer-correct public application surface); the WebRTC
  `IMediaTap` (`src/Client/WebRtc/IMediaTap.cs`) is a separate, later concern and not this contract.

## Decision

Expose media access to external modules as a **per-call tap** with these fixed properties:

1. **Two directed seams, not one duplex object.** `IMediaReceiver` observes inbound frames;
   `IMediaSender` injects outbound frames. Each attaches to exactly one call; attaching to a second
   call detaches from the first. `Detach()` and `Dispose()` stop delivery deterministically.

2. **Fan-out on the receive side.** Multiple receivers attach to the same call in parallel (default
   audio, recording, a realtime bridge); every attached receiver observes every inbound frame. One
   receiver's listener fault is isolated from the others.

3. **Synchronous delivery on the media thread — no backpressure.** `FrameReceived` fires inline on
   the RTP receive path. Handlers must buffer into their own queue and return immediately; blocking
   delays the receive path. There is deliberately **no** SDK-side backpressure or bounded queue for
   the tap: absorbing a slow consumer is the consumer's duty. This explicitly does *not* satisfy the
   CORE-301 "no backpressure effect at a slow consumer" acceptance criterion by the synchronous
   event alone — that is documented as a consumer obligation, with any `ChannelReader`/decode sugar
   left to a later teilpaket.

4. **Encoded frames, transport-only.** `MediaFrame` carries the codec payload as negotiated for the
   call, identified by RTP `PayloadType` plus `DurationRtpUnits`; the SDK neither decodes inbound nor
   encodes outbound on this contract. Consumers discover the format via `ICall.MediaParameters`.

5. **Send is gated by call state.** `SendAsync` drops frames while the call is not `Connected` or
   `OnHold`; it never buffers pre-connect frames.

## Consequences

Positive:

- Modules get a stable, `Infrastructure`-free way to do real media work; this is the concrete surface
  ADR-008 later relies on when it lists the media tap as an allowed third-party contract.
- The synchronous, unbuffered, encoded-frame model keeps the hot path allocation-lean and pushes all
  buffering/decoding cost into the consumer, consistent with the transport-only codec stance.

Tradeoffs:

- The blocking/no-backpressure contract is a sharp edge: a misbehaving handler stalls the receive
  path. It is mitigated only by the documented contract and by per-listener fault isolation, not by
  the SDK. A safer async/`ChannelReader` sugar remains deferred follow-up (CORE-301 teilpaket 2).
- Two single-attachment seams (receiver, sender) rather than a duplex session keeps each direction
  simple but means a duplex consumer wires two objects.

## Guardrails

- Tap types stay in `Core.Application.Media` (public application layer); no `Infrastructure/*` leaks
  into the contract, and modules must consume only this surface (ADR-008 condition 4).
- The "synchronous, must-not-block" event contract is part of the public XML doc and must not be
  weakened silently; any move to an async delivery model is a new, additive contract.
- Frame payloads remain encoded; introducing SDK-side decode/encode on this path is out of scope and
  would contradict the transport-only codec decision.
- Per-listener fault isolation on inbound fan-out must remain (one slow/failing receiver must not
  starve the others). Note: the isolation `catch` in `SipCoreCallChannel.DeliverInboundAudioFrame`
  was flagged during A3 as lacking logging (silent-catch rule) — follow-up, not a contract change.

## Sources

- `docs/archive/agent-log/2026-07-07-dev-a3.md` (CORE-301 teilpaket 1: media-tap contract, blocking
  vertrag, encoded-not-PCM, fan-out/detach/dispose, backpressure = consumer duty).
- `docs/archive/agent-log/2026-07-07-dev-a2.md` (module extension point that this tap serves).
- Code: `src/Core/Application/Media/IMediaReceiver.cs`, `IMediaSender.cs`, `MediaFrame.cs`.
- Related: ADR-007 (host/plugin split), ADR-008 (community module store — references this tap as a
  public module contract but does not design it).
