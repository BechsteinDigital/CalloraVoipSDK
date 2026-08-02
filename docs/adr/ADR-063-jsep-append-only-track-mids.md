# ADR-063: JSEP-Conformant Append-Only MIDs for Runtime-Added WebRTC Tracks

Status: Accepted
Date: 2026-08-01

## Context

The WebRTC facade lets a consumer add audio/video tracks at runtime (`IPeerConnection.AddAudioTrack` /
`AddVideoTrack`, 4.7.0 N-audio / N-video). Each call returns the track's numeric `a=mid`, which the consumer
then uses to address the track (send frames, request key frames). `WebRtcAddedTrackSet` assigns those MIDs and
`WebRtcSdpOptionsBuilder` lays the m-lines out for the offer; the two must agree or the handle addresses the
wrong m-line.

Until 4.7.2 the default ("legacy") layout grouped the m-lines by kind — primary audio, all added audio, primary
video, all added video — and derived each MID from its position in that grouped list. A stable-numeric opt-in
(`UseStableNumericMediaIds`, 4.7.1) offered the alternative append-only layout.

A pre-release review found the grouped layout is broken (Finding 2). Because an added video's MID depends on how
many added *audio* tracks precede it in the group, and audio can be added *after* the video, the MID handed back
at `AddVideoTrack` time drifts. Concretely: with no primary video, `AddVideoTrack()` returns `"1"`, and a later
`AddAudioTrack()` also returns `"1"` — the two handles collide, and after negotiation the video actually sits at
MID `"2"`, so `VideoTrack.SendFrameAsync` addresses the audio m-line. The bug was latent because every existing
test added tracks in "natural" (audio-before-video) order, where grouped and append-only layouts coincide.

The authoritative model is JSEP (RFC 8829 §5.2) / RFC 8843: a MID is a **stable identifier** assigned from a
monotonic counter at first negotiation and never changed; m-sections are **never reordered or removed** (a
closed one is kept `rejected` in place); new tracks are **appended** in `addTrack`/`addTransceiver` order, not
grouped by kind. Every reference stack does exactly this — libwebrtc/Chrome (`"0"`, `"1"`, …), Firefox
(`sdparta_N`), Pion and aiortc (monotonic counter). None groups m-lines by type. So the grouped layout is not
just buggy, it is **below reference parity**; the append-only ("stable") layout *is* the reference behaviour.

## Decision

Runtime-added tracks always use the stable, append-only numeric-MID layout, independent of the flag.

1. `WebRtcAddedTrackSet` assigns every added track a MID of `1 + primaryVideoCount + callOrder` — the primary
   audio (MID 0) and primary video(s) keep their MIDs, and each runtime track appends in global API call order,
   independent of its kind. Mixed audio/video add order can no longer collide or shift a MID.
2. `WebRtcSdpOptionsBuilder` routes any offer with runtime-added tracks through the append-only `StableMultiTrack`
   layout; the grouped `LegacyMultiTrack` path is removed.
3. `UseStableNumericMediaIds` is retained (public API, no SemVer break) but now governs only the *fixed 1+1*
   case: whether such a peer offers numeric MIDs or the historic semantic `audio`/`video` MIDs. Its default
   stays `false`, so a fixed 1+1 peer's SDP remains **byte-identical** to 4.6/4.7.1.

Related hardening in the same review round (Finding 1): recv-side simulcast attaches a per-RID depacketiser +
reorder buffer per RID (`BundledVideoTrack`) and the demux learns SSRC→MID/RID associations
(`BundledRtpDemultiplexer`). An authenticated peer can stamp a fresh RID/SSRC on every packet, so both are now
DoS-capped (RFC 8853 / ENGINEERING_RULES §132-133 wire-boundary caps): RID lanes above the cap drop the packet;
the learned tables resolve a new key for the packet without retaining an unbounded entry.

## Consequences

- **The Finding 2 collision is gone** and added-track MIDs are RFC 8829-stable, matching libwebrtc/Firefox/Pion.
  A new `WebRtcAddedTrackSetTests` regression covers the previously untested mixed (video-before-audio) order.
- **A behaviour change for the non-default legacy N-path.** A peer that added tracks with `UseStableNumericMediaIds`
  left at its default now emits append-only (call-order) m-lines instead of type-grouped ones. This is a bug fix
  (the grouped layout mis-addressed tracks) and is JSEP-conformant; the m-line *order* differs but MIDs stay a
  valid BUNDLE set. The fixed 1+1 SDP is unchanged, so SIP and simple WebRTC peers are unaffected.
- **No public API change.** `PublicApi.approved.txt` is unchanged; only the flag's documented meaning narrows.
- **Recv DoS surface bounded.** Simulcast senders with a handful of encodings are unaffected by the RID cap;
  only a peer flooding distinct RIDs/SSRCs is throttled.

## Alternatives considered

- **Keep the legacy layout as an explicit opt-in.** Rejected: it is a known-wrong, sub-reference behaviour with
  no consumer benefit; keeping a foot-gun opt-in only invites the same bug to be re-hit.
- **Decouple `a=mid` from the m-line index (carry an explicit MID token).** RFC-clean, but a larger change to the
  negotiator/serializer for no additional correctness over append-only, which the reference stacks already use.
- **Flip the default to stable and keep both paths.** Still ships a broken grouped path; a mixed-order add under
  the (now non-default) legacy flag would still collide. Removing the path is simpler and strictly safer.
