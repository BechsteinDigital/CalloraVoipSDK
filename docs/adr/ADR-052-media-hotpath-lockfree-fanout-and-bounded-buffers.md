# ADR-052: Media Hot-Path Concurrency — Lock-Free Fan-Out and Bounded Drop-Oldest Buffers

Status: Accepted
Date: 2026-07-08

## Context

The media hot path runs a packet or a frame every ~20 ms per stream for the entire life of a
call. Two of its steps hand data to code the SDK does not control, and one of its steps buffers
data between two threads that run at different rates:

1. **Fan-out to listeners/taps.** Inbound audio and video frames are delivered to any number of
   subscribed listeners (the public media-tap consumers, recording taps, bridge transcoders). The
   listener bodies are foreign code — arbitrary consumer callbacks that may be slow, may throw, and
   may add or remove themselves concurrently.
2. **Rate-mismatched buffering.** The RTP receive path is network-paced and bursty; the audio
   playback callback is hardware-paced (one frame per invocation); the video reorder/jitter path
   holds packets behind sequence gaps; NACK retransmission holds recently-sent packets. Each is a
   producer/consumer buffer between threads running at different speeds.

The concurrency hazards here differ from the signalling plane. Holding a lock while invoking a
foreign listener would let a slow or re-entrant consumer stall the RTP receive thread — jitter,
dropped packets, or deadlock if the callback re-enters the SDK. And an *unbounded* buffer on any
of these paths would, under sustained jitter or a stalled consumer, grow without limit: the failure
mode is not just memory but mouth-to-ear latency (a 10-second backlog plays 10 seconds late).

The governing rule is ENGINEERING_RULES **K3**: on the media hot path, no locks over foreign code,
no avoidable allocations (HARD-F1), bounded buffers with drop-oldest instead of unbounded queues
(HARD-F4), and copy-on-write arrays for tap/listener lists. This ADR records the *concurrency*
decision for that path — how foreign code is isolated and how backpressure is applied. The
*allocation* dimension (pooled receive buffers, span-based framing) is C07-01; the tap *API shape*
(encoded frames, per-call fan-out) is C18-01. This ADR is the thread-safety complement to both.

### Verified current state (graphify + code)

- **Fan-out snapshots the listener set, then runs foreign code with no lock held.**
  `SipCallChannelFrameTap.DeliverInbound` (`src/Core/Infrastructure/Sip/Adapters/SipCallChannelFrameTap.cs`,
  ~L40) takes the lock only long enough to copy the listeners into a stack array
  (`lock (_listenerSync) listeners = [.. _listeners];`), then iterates that array **outside** the
  lock, wrapping each `listener(frame)` in try/catch so one faulting listener is logged and the
  rest still run ("continuing with the remaining listeners"). Add/remove mutate the backing list
  under the same lock. Tests
  `CallVideoFrameContractTests.A_faulting_listener_does_not_prevent_the_others` and
  `..._Removed_listener_stops_receiving_without_affecting_others` pin the fault-isolation and
  live-mutation guarantees.
- **Audio playback buffer is bounded with drop-oldest.**
  `BoundedPlaybackBuffer` (`src/Audio/Abstractions/Processing/BoundedPlaybackBuffer.cs`) wraps a
  `Channel.CreateBounded<byte[]>` with `FullMode = BoundedChannelFullMode.DropOldest` and an
  eviction callback that does `Interlocked.Increment(ref _droppedFrames)`; `Enqueue` uses
  `TryWrite` (always succeeds under DropOldest). `LinuxAudioDevice` caps it at
  `PlaybackQueueCapacity = 50` frames (1 s @ 20 ms) with the HARD-F4 rationale in-comment.
- **Video reorder buffer is depth-bounded and skips forward.**
  `VideoReorderBuffer` (`src/Core/Infrastructure/Rtp/VideoReorderBuffer.cs`) bounds depth (1–16384)
  and, when the buffered count exceeds depth behind a gap, advances `_nextExpected` to the lowest
  buffered sequence — discarding the stale gap rather than growing.
- **NACK retransmission buffer is capacity-bounded FIFO.**
  `RtpRetransmissionBuffer` (`.../Rtp/Retransmission/RtpRetransmissionBuffer.cs`, default 512,
  max 32768) dequeues the oldest sequence when full under `lock (_sync)`.
- **HARD-F1/F4 markers exist and are localised.** HARD-F1 (no per-frame allocation) marks the
  cached G.722 codec instances (`WindowsAudioDevice`, `LinuxAudioDevice`), `PcmG722Codec`, and
  `PcmGain`; HARD-F4 (bounded drop-oldest) marks `BoundedPlaybackBuffer` and `LinuxAudioDevice`.

## Decision

The media hot path isolates foreign code and applies bounded backpressure by construction:

1. **Fan-out is lock-free over foreign code.** The listener set is captured under a short lock into
   a private snapshot array; foreign listener invocation then runs **outside** the lock. The RTP
   receive thread is therefore never held on a listener's execution time, and a listener cannot
   deadlock the SDK by re-entering the lock.
2. **A faulting listener is isolated.** Each invocation is wrapped in try/catch-and-log; one
   listener's exception never denies delivery to the others and never propagates onto the media
   thread. (This is the hot-path counterpart to the "handlers must not throw" contract in C14-01,
   which cannot be enforced against foreign code — so it is contained instead.)
3. **Every inter-thread media buffer is bounded with a drop policy — never unbounded.** Drop-oldest
   is the jitter-buffer-correct choice on the playout path (evict stale frames so latency stays
   bounded and playback stays fresh); the reorder buffer skips forward past a stale gap; the NACK
   buffer is bounded FIFO. Overflow is a counted, observable event, not silent growth and not
   backpressure that stalls the producer.
4. **No avoidable allocation on the per-frame path (HARD-F1)** — codec and gain helpers are stateless
   and reused rather than allocated per frame. (Buffer-pooling detail is C07-01.)

### Crux

The decision that ties the path together is that **the hot path treats both foreign code and
overflow as things to bound, not to wait on.** Foreign listener code is bounded by snapshotting the
set and dropping the lock before calling it (time isolation) and by catching its faults (failure
isolation). Overflow is bounded by dropping the *oldest* datum, because on a real-time media path a
stale frame is worthless — keeping it only adds latency. Both choices refuse the tempting
alternative (hold the lock / grow the queue) precisely because that alternative converts a transient
consumer problem into an unbounded producer stall.

## Consequences

Positive: the RTP receive thread runs at a bounded, predictable cost regardless of how many
listeners are attached or how they behave; buffered latency has a hard ceiling; overflow is
observable via drop counters; no media-path lock is ever held across untrusted code.

Tradeoffs and honest divergences:

- **"Copy-on-write" is the intent, not the literal mechanism.** The K3 wording says *copy-on-write
  arrays* for listener lists. The implementation (`SipCallChannelFrameTap`) is not a persistent
  immutable array swapped by `Interlocked.Exchange`; it is a mutable `List<>` under a lock that is
  **snapshot-copied into a fresh array on every delivery**. The guarantee is equivalent (the
  hot-path iteration sees a stable snapshot and never holds the lock over foreign code), but there
  is a per-delivery array allocation, which is in mild tension with HARD-F1's allocation goal. That
  is an accepted trade: correctness of fan-out over cheapness of a small array copy, and the
  allocation is bounded by listener count, not by traffic content.
- **Drop-oldest is lossy by design.** Under sustained overload the SDK silently discards media
  (counted, not logged per drop). That is correct for real-time playout but means the tap/consumer
  is not guaranteed lossless delivery under overload — a consumer that needs every frame must keep
  up.
- **The three buffers use three different bound mechanisms** (bounded channel / depth-skip / FIFO
  cap) suited to their semantics rather than one shared type; this is deliberate, but it means
  "bounded drop-oldest" is a family of policies, not a single implementation, and HARD-F4 markers
  are present on only the audio playout members.
- **Nothing gates this mechanically.** No architecture test forbids an unbounded queue or a
  lock-held-over-callback on a new media path; the contract is review-enforced.

## Guardrails

- New media-path fan-out snapshots the subscriber set and invokes foreign code outside any lock,
  with per-invocation fault isolation; reviewers reject a lock held across a listener/tap callback.
- New inter-thread media buffers are bounded and carry an explicit overflow policy (drop-oldest by
  default on playout paths) plus a drop counter; an unbounded `Queue`/`List` on a media path is a
  review finding and should carry a HARD-F4 marker with rationale.
- Per-frame code reuses stateless helpers rather than allocating per frame (HARD-F1); see C07-01 for
  the buffer-pooling companion rules.
- Overflow is observable (a counter), never a producer stall and never silent unbounded growth.

## Sources

- ENGINEERING_RULES.md — K3 (media hot path: no locks over foreign code, HARD-F1 no-alloc,
  HARD-F4 bounded drop-oldest, copy-on-write tap/listener lists).
- docs/thread-memory-safety-analysis.md — unbounded-collections and hot-path-lock catalogue that
  this contract closes; MediaReceiver AttachToCall TOCTOU noted as the lifetime companion.
- Code (graphify-oriented, then read):
  `src/Core/Infrastructure/Sip/Adapters/SipCallChannelFrameTap.cs` (snapshot fan-out + fault isolation);
  `src/Audio/Abstractions/Processing/BoundedPlaybackBuffer.cs` and
  `src/Audio/Linux/Infrastructure/LinuxAudioDevice.cs` (bounded drop-oldest, HARD-F4);
  `src/Core/Infrastructure/Rtp/VideoReorderBuffer.cs` (depth-bound skip-forward);
  `src/Core/Infrastructure/Rtp/Retransmission/RtpRetransmissionBuffer.cs` (bounded FIFO);
  `src/Core/Application/Media/Sessions/PcmG722Codec.cs`, `src/Audio/Abstractions/Processing/PcmGain.cs` (HARD-F1);
  `tests/CalloraVoipSdk.Core.IntegrationTests/CallVideoFrameContractTests.cs` (fault-isolation, live-mutation).
- Related ADRs: C14-01 (event-dispatch/object-lifecycle concurrency), C07-01 (allocation avoidance),
  C18-01 (public media-tap contract).
