# ADR-022: INVITE Transaction Robustness — Retransmission, Fork, 100rel, Codec

Status: Accepted
Date: 2026-07-09

## Context

Several distinct INVITE-side failures against real peers (sipgate, Fritz!Box, an OpenAI
G.711 transport) shared one theme: the SDK's INVITE-transaction handling misclassified or
mishandled ordinary UAS situations, breaking call establishment.

1. **Double-INVITE misread as a merge.** `SipMergedInviteTracker.TryBuildMergeKey` keyed the
   merge on `Call-ID | From-tag | CSeq | INVITE` **without the top Via branch**. RFC 3261
   §8.2.2.2 (with §17.2.3 matching) defines a merged request only when the identity tuple
   matches *and* the top-Via branch **differs**. A second INVITE with the **same** branch is a
   retransmission (§17.2.1). The branch-agnostic key flagged every repeat — including genuine
   retransmissions — as a merge, so the SDK wrongly answered **482 Loop Detected**.
2. **Fork-handling failure logged at Debug.** The fire-and-forget fork ACK/BYE path
   (`SipCallSessionTransactionService.AcknowledgeForkAndMaybeTerminateAsync`) swallowed all
   exceptions into a `LogDebug` catch — invisible, though a failed fork-ACK retransmits the 2xx
   to timeout and a failed fork-BYE leaks a hung call-leg on the non-selected UAS.
3. **`Supported: 100rel` treated as `Require`.** `ShouldUseReliableProvisional` opted into a
   reliable 180 with `Require: 100rel` on merely-`Supported`; when the box sent no PRACK, the
   200 OK hung behind retransmit timeouts ("it keeps ringing").
4. **Codec negotiation produced an audio-less answer.** G.722 was hard-pinned rank 0; and a box
   listing PCMU/PCMA/G722 as static PTs *without* rtpmap yielded an answer containing only
   `telephone-event` (488 "SDP problem"), because name-based intersection never matched the
   "PT<n>" placeholder names.

### Verified current state (graphify + logs)

- `SipMergedInviteTracker` (`src/Core/Infrastructure/Sip/Signaling/Ingress/SipMergedInviteTracker.cs`,
  L13) exposes `.IsMergedInvite()` / `.TryBuildMergeKey()` / `.PruneExpired()` and is referenced
  by `SipCallSignalingService`. `_seen` stores `(string? Branch, DateTimeOffset SeenAt)`;
  `IsMergedInvite` extracts the top-Via branch via `SipProtocol.ExtractViaBranch` — same tuple +
  **different** branch → merged (482); **same** branch (or unparseable on one side) →
  retransmission (false, TTL refreshed). Identity key unchanged; branch is a compare value, not a
  key part.
- Fork-error catch in `SipCallSessionTransactionService` logs `LogWarning` (was `LogDebug`);
  no control-flow change.
- 100rel opt-in (`ShouldUseReliableProvisional`) fires only on explicit `Require: 100rel`
  (RFC 3262 §3), not on `Supported`.
- Codec preference: `SdpMediaNegotiationOptions.PreferredCodecNames` orders/filters offer and
  answer (telephone-event always retained; no match → defaults); `ResolveEffectiveName`
  (PT 0/8/9 → PCMU/PCMA/G722) runs before the identity compare in
  `SdpOfferAnswerNegotiator.NegotiateCodecs` so static-PT offers without rtpmap still match.
  Public surface: `SdkConfiguration.PreferredAudioCodecs` → `VoipClient` → line/call channel.

## Decision

Classify INVITE-transaction situations by the RFC's own discriminators and fail loud where an
operator needs to see it:

1. **Merge vs. retransmission by top-Via branch.** Same identity tuple + different branch =
   merge (482); same branch = retransmission (route into the normal ingress path).
2. **Fork-handling errors log at Warning**, not Debug.
3. **Reliable provisional only on `Require: 100rel`** (§3262 §3), never on `Supported`.
4. **Codec selection honours a configured preference and resolves static PTs to names** before
   intersection; telephone-event is preserved; empty audio answers are prevented.

Crux: each fix restores an RFC discriminator the code had collapsed — branch for merge/retx,
`Require` vs `Supported` for 100rel, canonical codec name for static PTs.

## Consequences

Positive: retransmitted (same-branch) out-of-dialog INVITEs are no longer 482'd; genuine merges
(other branch) still 482 (RFC-correct); fork failures are operationally visible; ringing no
longer hangs on a phantom 100rel requirement; codec answers always contain a real audio codec
and honour operator preference.

Tradeoffs / honest limits: if a peer retransmits with a **different** branch, 482 remains
RFC-correct — so the double-INVITE class is not universally "solved"; a residual case would point
elsewhere. Codec preference and 100rel are audio/SDP-adjacent decisions folded here because they
were part of the same call-establishment failure class in the M1 hotfix log; the SDP negotiation
machinery itself is the concern of the SDP cluster (C03). Real-call efficacy stayed founder
real-tests. Note: a separate SDES crypto-mirroring bug was flagged in the same log as
out-of-scope follow-up (see C04/security).

## Guardrails

- Merge detection MUST use the top-Via branch as the discriminator; a same-branch repeat is a
  retransmission, never a merge.
- Fork ACK/BYE failures MUST be logged at Warning or higher.
- Reliable provisional responses MUST require explicit `Require: 100rel`.
- A codec answer MUST NOT be telephone-event-only when the offer carries a usable audio codec;
  static PTs (0/8/9) resolve to canonical names before intersection.

## Sources

- Logs: docs/archive/agent-log/2026-07-09-dev-b7-double-invite-retransmission.md;
  docs/archive/agent-log/2026-07-09-dev-b7-fork-error-warning.md;
  docs/archive/agent-log/2026-07-08-dev-m1-hotfix.md (100rel opt-in + codec preference nachträge)
- Code: `SipMergedInviteTracker` (src/Core/Infrastructure/Sip/Signaling/Ingress/SipMergedInviteTracker.cs);
  `SipCallSessionTransactionService` (fork-error catch);
  `ShouldUseReliableProvisional`; `SdpOfferAnswerNegotiator.NegotiateCodecs` /
  `ResolveEffectiveName`; `SdpMediaNegotiationOptions.PreferredCodecNames`
- Tests: SipMergedInviteTrackerTests; SipInviteSuccessAckTests; ReliableProvisionalOptInTests;
  codec-preference tests (m1-hotfix)
- Marker: RFC 3261 §8.2.2.2, §17.2.1, §17.2.3; RFC 3262 §3; B.7
