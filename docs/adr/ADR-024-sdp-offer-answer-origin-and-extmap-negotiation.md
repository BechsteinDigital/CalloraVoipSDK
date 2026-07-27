# ADR-024: SDP Offer/Answer Negotiation — Origin Versioning and extmap Id Assignment

Status: Accepted
Date: 2026-07-14

## Context

The SDK builds and answers SDP for every SIP call leg. Two negotiation semantics govern how a
remote peer interprets what we send, and both were originally hard-coded in a way that broke
interop or blocked a feature:

- **Origin line (`o=`).** The origin field carried a constant `o=- 0 0 IN ...`. RFC 3264 §5 lets a
  peer detect a session modification (hold, re-INVITE) by observing the **incremented sess-version**
  in `o=`. A constant `0 0` means foreign PBXs never see a hold/re-INVITE as a change — an interop
  break (this was the concrete failure that motivated the change).
- **Header extensions (`a=extmap`).** RTP header extensions (RFC 8285) need their one-byte ids
  negotiated in offer/answer so both sides agree which id carries which URI. Without an extmap
  negotiation surface, congestion control (transport-wide sequence number), BUNDLE MID routing, and
  simulcast RID could not place their extensions on the wire.

Both live on the same signaling surface — the SDP the SDK emits in an offer and echoes in an answer —
and flow through the same `SdpOfferAnswerNegotiator` + serializer path, so they are decided together.

### Verified current state (graphify-grounded)

- `SdpSessionDescription` (`src/Core/Infrastructure/Sdp/Models/SdpSessionDescription.cs`) carries
  `SessionId` and `SessionVersion` (`long`, both marked RFC 4566 §5.2 in XML doc). The serializer
  (`SdpSessionSerializer.cs:24`) emits `o=- {SessionId} {SessionVersion} IN {netType} {addr}`.
- The origin values are threaded through the option DTOs: `SdpMediaNegotiationOptions` (public port)
  → `SdpMediaOptions` (`SessionId`/`SessionVersion`, RFC 4566 §5.2 in doc) → `CreateOffer` /
  `NegotiateAnswer` / disabled-answer builders in `SdpOfferAnswerNegotiator`.
- `SipCoreCallChannel` owns the origin identity per call leg
  (`src/Core/Infrastructure/Sip/Adapters/SipCoreCallChannel.cs`): a process-wide monotonic seed
  (`_sessionIdSeed = UtcNow.ToUnixTimeSeconds()`, `Interlocked.Increment`) gives each channel a
  **stable, unique sess-id** (`_sdpSessionId`, L94); `BuildLocalSdpOptions()` (L785) sets
  `SessionVersion = Interlocked.Increment(ref _sdpSessionVersion)` (L803) on every local SDP build.
  Retransmits reuse the cached SDP string (no rebuild → version stays stable).
- `SdpExtmap` (`src/Core/Infrastructure/Sdp/Models/SdpExtmap.cs`): `Id` + optional `Direction` +
  `Uri`, with `TryParse`/`Serialize`. Parser adds an `extmap` case
  (`SdpSessionParser.cs:171`), the serializer emits `a=extmap:{...}` per m-line
  (`SdpSessionSerializer.cs:113`), and `MediaBuilder` carries a per-m-line `Extensions` list.
- `SdpOfferAnswerNegotiator` performs the negotiation:
  - `BuildOfferExtmaps` (L571) assigns sequential one-byte ids `1..14`
    (`OneByteMaxExtensionId = 14`, RFC 8285 §4.2: 0 is padding, 15 reserved) to the supported URIs;
    empty list ⇒ no `a=extmap`.
  - `BuildAnswerExtmaps` (L608) echoes the **offered id** for each offered URI we support and drops
    the rest (RFC 8285 §5: the offerer owns id assignment); only one-byte ids are echoed.
  - Beyond the original transport-cc scope, the same machinery now also carries **MID**
    (`WithMidExtension`, RFC 8843 §9 / RFC 9143) and **RID** for BUNDLE and send-side simulcast
    (RFC 8853): `BundledExtmapUris` prepends `RtpHeaderExtensionUris.Mid` (and `.Rid` for simulcast)
    so the answer mirrors the offered id on every m-line.
- Both surfaces are **default-off / backward-compatible**: no session options ⇒ `o= 0 0`; empty
  `HeaderExtensionUris` and non-BUNDLE ⇒ no `a=extmap` emitted.

## Decision

Treat the SDP the SDK emits in an offer and echoes in an answer as a negotiated contract, and encode
two rules on the shared `SdpOfferAnswerNegotiator` + serializer path:

1. **Origin identity is per-leg and modification-signalling.** Each `SipCoreCallChannel` (call leg)
   owns a **stable sess-id** for its lifetime and an **incrementing sess-version**. The version is
   bumped on every locally built SDP (offer, answer, hold, unhold) so a remote peer detects the
   modification per RFC 3264 §5; retransmits reuse the cached SDP string and therefore keep the same
   version. Origin values are optional inputs to the negotiator (public
   `SdpMediaNegotiationOptions`), preserving the `0 0` default for callers that do not supply them.

2. **The offerer owns extmap id assignment; the answerer echoes.** The offer assigns sequential
   one-byte ids (1..14) to its supported header-extension URIs; the answer echoes the offered id for
   each URI it supports and silently drops unsupported or non-one-byte (reserved 0/15) ids. The same
   negotiation carries transport-cc, MID (BUNDLE), and RID (simulcast). extmap emission is opt-in.

### Crux

The subtle part is *when* origin version changes and *who* owns extmap ids.

- **Version ownership lives at the channel, not the SDP builder.** The sess-id/version pair is state
  of the call leg, so it belongs to `SipCoreCallChannel` (which knows about retransmits vs. genuine
  re-builds), not to the stateless negotiator. The negotiator only stamps whatever id/version it is
  handed. This keeps the negotiator pure and makes "same leg → same id, bumped version" a property of
  the channel's build/cache discipline.
- **The offerer, not the answerer, is the id authority (RFC 8285 §5).** The answer never invents an
  id; it can only echo an offered one or drop it. Modelling MID/RID as "prepend the URI to the
  supported set" makes the generic `BuildAnswerExtmaps` echo them at the offered id automatically —
  one negotiation path serves transport-cc, BUNDLE, and simulcast without special cases.

## Consequences

Positive: hold/re-INVITE are now observable to remote peers (the interop precondition B.5 was built
for); header extensions have a real offer/answer negotiation surface that later slices (transport-cc,
BUNDLE routing, simulcast) plug into without touching the parser/serializer. Both changes are additive
and default-off, so existing non-BUNDLE, non-extension SDP is byte-identical.

Honest divergence:

- **Version increments unconditionally per local build, not strictly "on change."** RFC 4566 §5.2 and
  RFC 3264 §5 specify that sess-version increments *only when the session description actually
  changes*. The implementation increments on every `BuildLocalSdpOptions()` call and relies on the
  **retransmit cache** (same SDP string → no rebuild → no bump) to approximate that rule. A genuine
  re-build that happens to produce an identical description would still bump the version. This is a
  deliberate simplification: over-signalling a change is interop-safe (peers re-evaluate an unchanged
  offer harmlessly), under-signalling is not. It is not a strict RFC "only-on-change" implementation.
- **Interop is not claimed.** The B.5 log is explicit: the change makes the origin line *correct and
  test/wire-proven*, but "hold/re-INVITE interop against a foreign PBX" remains an unverified real-test
  item. This ADR records the negotiation semantics, not an interop guarantee.
- **One-byte extmap only.** Only the one-byte header form (ids 1..14) is offered and echoed; the
  two-byte form (RFC 8285 §4.3) is not negotiated. URIs beyond the first 14 are dropped. Acceptable
  because the SDK's supported extension set is small.
- **extmap is a signalling surface, not yet a data path in the base slice.** The negotiation exists
  independently of stamping bytes into RTP; the transport-cc data path was tracked as follow-up. The
  SDK does not over-offer an extension it cannot yet honour end-to-end (BUNDLE MID and simulcast RID
  are the wired consumers today).

## Guardrails

- Non-BUNDLE, no-extension SDP stays byte-identical: no origin options ⇒ `o= 0 0`; empty
  `HeaderExtensionUris` and non-BUNDLE ⇒ no `a=extmap`. Verified by regression tests
  (`SdpOriginVersionTests`, `VideoExtmapNegotiationTests`).
- Origin: same call leg → same sess-id across successive local SDPs; version strictly increases per
  local build; distinct channels → distinct sess-ids (`SdpOriginVersionTests`, 5 tests).
- extmap: the answer echoes only offered ids for supported URIs, drops unsupported URIs and reserved
  ids 0/15 (one-byte range guard), and the offerer's id is never rewritten by the answer
  (`VideoExtmapNegotiationTests`, 11 tests incl. the id-0/15 boundary).
- The sess-version bump must remain coupled to the SDP-build/retransmit-cache path in
  `SipCoreCallChannel`; moving version state into the stateless negotiator would break the
  "retransmit keeps the version" invariant.

## Sources

- Logs: `docs/archive/agent-log/2026-07-08-dev-b5-sdp-origin-version.md` (B.5 origin/version,
  commit 889a9f5); `docs/archive/agent-log/2026-07-14-dev-sdp-extmap-negotiation.md` (extmap
  negotiation, commit 172c7ad, merged 4523a3f).
- Code: `src/Core/Infrastructure/Sdp/Models/SdpSessionDescription.cs` (SessionId/SessionVersion);
  `src/Core/Infrastructure/Sdp/Parsing/SdpSessionSerializer.cs` (o= + a=extmap emit);
  `src/Core/Infrastructure/Sdp/Parsing/SdpSessionParser.cs` (extmap parse);
  `src/Core/Infrastructure/Sdp/Parsing/MediaBuilder.cs` (per-m-line Extensions);
  `src/Core/Infrastructure/Sdp/Models/SdpExtmap.cs`;
  `src/Core/Infrastructure/Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs` (BuildOfferExtmaps L571,
  BuildAnswerExtmaps L608, WithMidExtension L602, OneByteMaxExtensionId L567);
  `src/Core/Infrastructure/Sdp/OfferAnswer/SdpMediaOptions.cs` (SessionId/SessionVersion);
  `src/Core/Application/Ports/Sdp/SdpMediaNegotiationOptions.cs` (public port);
  `src/Core/Infrastructure/Sip/Adapters/SipCoreCallChannel.cs` (_sdpSessionId L94,
  BuildLocalSdpOptions L785, version bump L803).
- Tests: `SdpOriginVersionTests`; `VideoExtmapNegotiationTests`.
- RFC/Marker: RFC 4566 §5.2 (o= sess-id/sess-version); RFC 3264 §5 (version-increment
  modification semantics); RFC 8285 §4.2/§4.3/§5 (one-byte/two-byte header extensions, offerer owns
  id assignment); RFC 8843 §9 / RFC 9143 (MID); RFC 8853 (simulcast RID).
