# ADR-039: Transport-Wide Congestion Control — Estimator, AIMD Rate Policy, and Recommended-Bitrate API

Status: Accepted
Date: 2026-07-15

## Context

The transport-cc feedback plane ([C10-01]) gives the media sender an inbound RTCP report of
per-packet arrivals and gaps. This ADR covers the **sender-side estimator** that turns those
reports into a congestion signal, the **rate policy** that turns the signal into a target
bitrate, and the **public API** that hands that recommendation to the application. The founder
decision (after comparing SipSorcery/baresip/Ozeki, none of which ship full GCC) was **Option 1:
expose a ready-to-use recommendation rather than raw metrics, with a deliberately simple
controller**; a smoother standards-based controller (SCReAM, RFC 8298) is a later opt-in upgrade.

The SDK is **transport-only**: it never encodes. The recommendation tells the application what
bitrate to set on *its* encoder.

### Verified current state

Code-grounded via graphify (`query "recommended bitrate congestion estimate"`,
`query "CongestionBitrateController recommended bitrate wiring"`) plus reads:

- **Delay signal.** `TransportCcSendHistory` (bounded direct-mapped ring, seq%capacity) records
  each stamped packet's send time. `TransportCcFeedbackInterpreter.Interpret` reconstructs each
  received packet's arrival time (`referenceTicks*64000 + Σ delta*250`, overflow-safe) into
  `TransportCcFeedbackResult`. `TransportCcFeedbackCorrelator.Correlate` joins reconstructed
  arrivals with the send history into per-packet delay gradients `(Δarrival − Δsend)`
  (offset-invariant; gaps/evicted skipped). Time units (64 ms / 250 µs) are centralised in
  `TransportCcTime`.
- **Delay trend + classification.** `TransportCcDelayTrendEstimator.Observe` folds each gradient
  into an EWMA (`trend = trend*(1-α) + grad*α`) and classifies against a fixed ± threshold into
  `CongestionSignal` {Normal, Overusing, Underusing}, read/written under a `_sync` lock (no torn
  double read). `TransportCcLossEstimator` smooths the loss ratio (EWMA) from the same results.
- **AIMD rate policy — NEW since the C10 logs.** `CongestionBitrateController` maps
  (signal, loss) → a target video bitrate with a simple AIMD rule: multiplicative back-off when
  `Overusing` or loss ≥ threshold, additive probe upward otherwise, clamped to [min, max].
  Stateful, thread-safe (`_sync`). This type does **not** appear in any C10 log — the logs end
  with an internal `Congestion` property and an explicitly *deferred* bitrate decision.
- **Sender-side orchestrator.** `TransportCcCongestionController` wires it together:
  `OnPacketSent` → `SendHistory.Record`; `OnRtcpPackets` (the compound already decoded once by
  the session — cf. commit 26816e5) → Interpret → Correlate → `DelayTrend.Observe` +
  `Loss.Observe` → `bitrate.Update`. It exposes `RecommendedBitrateBps`, `Quality`
  (`NetworkQuality` from signal+loss bands: Poor ≥10 % loss or Overusing, Fair ≥2 %, else Good),
  raw `Signal`/`DelayTrendMicros`/`LossRatio`, and a `RecommendedBitrateChanged` event
  (fires on the RTP control thread; subscriber faults caught+logged).
- **Wiring in `VideoRtpStream`.** Constructed gate-off (extmap-gated) with tuning constants
  (send-history cap 4096, delay EWMA 0.1, overuse threshold 5 ms, loss EWMA 0.1);
  `PacketSent += OnPacketSent`, `RtcpCompoundReceived += OnRtcpPackets`,
  `RecommendedBitrateChanged += OnCongestionRecommendationChanged`; symmetric Dispose-unsubscribe
  (`VideoRtpStream.cs:227-239`, `490-494`). Exposes `RecommendedBitrateBps` / `NetworkQuality` /
  `CongestionUpdated`.
- **Public API — NEW since the C10 logs.** `CallMediaOrchestrator.cs:240-241` calls
  `sdkCall.SetVideoCongestion(video.RecommendedBitrateBps, video.NetworkQuality)`, surfacing the
  recommendation to `ICall`. The application-facing contract is
  `IVideoSender.RecommendedBitrateChanged` → `VideoBitrateRecommendationEventArgs`
  (`RecommendedBitrateBps` + `NetworkQuality`, both nullable = "inactive for this leg"). The
  public domain enum is `NetworkQuality` {Good, Fair, Poor}. The C10 logs deferred this API
  entirely (pending the facade-naming decision).

## Decision

1. **Estimator reconstructs delay and loss; policy is a separate object.** The delay chain
   (send-history → interpreter → correlator → EWMA trend + threshold signal) and the loss
   estimator are pure/neutral building blocks. `CongestionBitrateController` holds the AIMD
   policy alone — "detection quality lives in the estimators; this only maps a signal to a
   bitrate."
2. **Ship a recommendation, not raw metrics (Option 1).** The public surface is a ready-to-use
   `RecommendedBitrateBps` plus a coarse `NetworkQuality`, delivered by event
   (`RecommendedBitrateChanged` / `VideoBitrateRecommendationEventArgs`). Raw signal/trend/loss
   remain readable but are not the primary contract. The application sets its encoder to the
   recommended value; the SDK never encodes.
3. **Deliberately simple AIMD now, SCReAM later.** A fixed-threshold EWMA detector + AIMD rate
   rule, not GCC's adaptive-threshold regression or SCReAM's rate control. RFC 8298 / adaptive
   threshold + hysteresis is the marked accuracy upgrade, opt-in behind an explicit quality goal.
4. **Gate-off wiring, opportunistic activation.** The controller is constructed only when the
   transport-cc extmap was negotiated; a non-supporting peer leaves the video stream unchanged.

### Crux

The product bet is **abstraction over raw telemetry**: external developers get one number to set
on their encoder and one enum for a UI hint, not a delay-gradient stream to interpret. The cost
is a simple controller whose tuning (α, thresholds, AIMD steps) is hand-picked, not adaptive —
accepted as the Option-1 baseline, with SCReAM as the escape hatch when accuracy matters.

## Consequences

Positive: the full sender loop is live and E2E-proven (`TransportCcCongestionWiringE2eTests`: a
real session with 100 ms peer-feedback steps drives the signal to Overusing across both seams).
The public API is minimal and transport-only-consistent; a plugin sets `encoder.SetBitrate(e.
RecommendedBitrateBps)` and reads `NetworkQuality` for UI.

Honest divergences and limitations:

- **Two of the four decision pillars post-date the C10 logs.** `CongestionBitrateController`
  (AIMD policy) and the public `RecommendedBitrateChanged` / `VideoBitrateRecommendationEventArgs`
  / `NetworkQuality` surface do **not** appear in any of the eleven logs — the logs end with an
  internal `Congestion` property and a *deferred* bitrate/API decision. This ADR captures the
  realised design; the logs capture the point before it landed.
- **Recommendation is NOT in `CallQualitySnapshot`.** The recommendation flows via the
  video-sender event / `ICall.SetVideoCongestion`, separate from `CallQualitySnapshot` (which
  carries jitter/loss/RTT/MOS). A reader looking there for the bitrate will not find it — this is
  intentional (per-video-leg vs. per-call quality snapshot) but easy to miss.
- **No hysteresis.** The fixed-threshold signal can oscillate at the boundary; EWMA smooths but
  does not eliminate it. Hysteresis is bundled with the adaptive-threshold/SCReAM upgrade.
- **Tuning is un-calibrated.** The α/threshold/AIMD-step constants are reasonable defaults, not
  network-tuned; calibration is a marked follow-up.
- **Sender-side only.** The estimator drives the *outbound* video bitrate (we send media, peer
  feeds back). It is video-only (audio has no transport-cc stamping) and single-stream.

## Guardrails

- The bitrate result stays clamped to [min, max] and thread-safe (feedback loop writes while the
  application reads); paired reads use the locked accessors, never field-by-field.
- The controller subscribes and unsubscribes symmetrically on Dispose (no leaked handler on the
  RTP control thread); subscriber exceptions are isolated (caught+logged), never propagated onto
  the media thread.
- The public contract exposes a **recommendation** (bitrate + `NetworkQuality`), not raw
  estimator internals; internal metrics may change without breaking the API.
- Any future GCC/SCReAM controller slots in behind the same `CongestionBitrateController` seam —
  the estimator and public API stay unchanged.

## Sources

- Logs (`docs/archive/agent-log/`): `2026-07-15-dev-transport-cc-delay-signal.md`,
  `…-transport-cc-delay-trend.md`, `…-transport-cc-feedback-interpreter.md`,
  `…-transport-cc-congestion-wiring.md` (+ feedback-plane logs in [C10-01]).
- Code: `Infrastructure/Rtp/CongestionControl/{TransportCcSendHistory,TransportCcFeedbackInterpreter,
  TransportCcFeedbackCorrelator,TransportCcDelaySample,TransportCcDelayTrendEstimator,
  TransportCcLossEstimator,CongestionSignal,CongestionBitrateController,
  TransportCcCongestionController,TransportCcTime}.cs`,
  `Infrastructure/Rtp/VideoRtpStream.cs:227-239,490-494`,
  `Application/Media/{CallMediaOrchestrator.cs:240,IVideoSender.cs,VideoBitrateRecommendationEventArgs.cs}`,
  `Domain/Calls/{NetworkQuality.cs,ICall.cs,CallQualitySnapshot.cs}`.
- Tests: `TransportCcCongestionControllerTests`, `TransportCcCongestionWiringE2eTests`,
  `CongestionBitrateControllerTests`, `TransportCcExtmapOfferTests`.
- RFC/marker: draft-holmer-rmcat-transport-wide-cc-extensions-01 (feedback consumed), RFC 8298
  (SCReAM, marked upgrade), founder "Option 1" decision
  (`project_video_interop_codec_decision`, `project_public_api_dx`).
