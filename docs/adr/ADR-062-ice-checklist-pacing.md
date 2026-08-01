# ADR-062: Globally Paced, Overlapping ICE Connectivity-Check Checklist

Status: Accepted
Date: 2026-08-01

## Context

The bundled WebRTC media leg runs ICE connectivity checks over the shared media socket after the transport's
receive loop is up (`IceMediaAttachment` → `IceNominationDriver` → `IceMediaConsentSession`, RFC 8445 §7).
Until 4.7.1 the controlling agent drove those checks as a single background loop:

- it selected one candidate pair, `await`ed its ordinary connectivity check to completion, and on success
  `await`ed a second USE-CANDIDATE check — strictly **one pair at a time**;
- between attempts it slept a fixed round delay (200 ms);
- the check primitive (`IceMediaConsentSession.SendCheckAsync`) sent the request **once** and waited out the
  full check timeout (2 s) on loss — there was no transaction-level retransmission;
- only the **controlling** agent checked; the controlled agent waited passively to adopt the peer's nomination.

The consequence is a latency bug in exactly the case ICE exists for. When the highest-priority pair is
unreachable (a host address on an unroutable interface, a server-reflexive candidate behind a symmetric NAT),
the serial loop spends that pair's entire timeout budget before the next pair — often the one that actually
works — gets its first check. A single lost packet compounds it: with no retransmission, the pair waits the
2 s timeout, then retries a whole round later. Real setup times of several seconds were observed on networks
where a reachable relay or srflx pair sat behind an unreachable host pair. 4.7.1 softened the ordering (check a
lower-priority reachable pair before an unreachable higher-priority one burns another round) but kept the
serial, non-retransmitting core.

The crux: connectivity checks are independent STUN transactions that should progress **in parallel** under a
global rate limit, and loss recovery belongs inside each transaction — not in a coarse per-pair retry loop.
RFC 8445 already specifies exactly this shape (§6.1.4 pacing, §14 `Ta`, §7.2.5 validation, §8 nomination), and
RFC 8489 §6.1 specifies the retransmission schedule.

## Decision

Replace the serial loop with a bounded, phase-driven checklist whose check *starts* are globally paced while
the transactions themselves overlap.

1. **`IceConnectivityCheckPacer`** — one bounded scheduler per media attachment. It starts at most one check
   per pacing interval (`Ta`, default 50 ms) but tracks each started transaction as in-flight and does not
   await it before starting the next. Work is classified `Triggered` > `Nomination` > `Ordinary` (RFC 8445
   §6.1.4.2, §7.3.1.4): triggered checks are FIFO and preempt everything, nomination and ordinary work are
   ordered by pair priority. The queue is DoS-bounded (512 default) and the drain loop **self-starts** on first
   enqueue, so a reactive triggered check learned off the receive loop dispatches even before `Start()`.
2. **Explicit checklist phases** — `IceNominationPairPhase` (`Frozen → Waiting → InProgress → Succeeded /
   Failed → Nominating → Nominated`) on each `IceNominationPairState`, replacing the previous `Attempts` /
   `Done` counters. Selection and nomination are pure functions of phase + pair priority.
3. **Transaction-level retransmission** — `IceMediaConsentSession.SendCheckAsync` retransmits the request with
   the **same** transaction id on an RFC 8489 §6.1 schedule (bounded to 3 transmissions within the check
   timeout) instead of relying on a per-pair retry. The checklist scheduler no longer owns loss recovery.
4. **Both roles check; only controlling nominates** — every agent runs ordinary checks (RFC 8445 §7.2). The
   controlling role additionally performs regular nomination (§8.1.1). Role conflicts (§7.3.1.1) re-compute all
   pair priorities via `SetRole` and redirect an in-flight nomination.
5. **Regular nomination, gated on a settled checklist** — the highest-priority *validated* pair is nominated
   only once no higher-priority pair is still `Frozen`/`Waiting`/`InProgress`. First nomination wins for the
   ICE generation; a later path change requires an ICE restart, not a second unnegotiated USE-CANDIDATE.

## Consequences

- **Faster, loss-tolerant setup.** Reachable pairs are no longer serialised behind an unreachable pair's
  timeout; ordinary loss recovers in hundreds of milliseconds. This is the intended fix.
- **No public surface change.** The rework is entirely within `Infrastructure/Stun/Ice`; `PublicApi.approved.txt`
  is unchanged and negotiated SDP/wire behaviour is byte-identical. Existing ICE tests plus new pacer,
  triggered-check and late-candidate tests cover it; the full suite is green across net8/net9/net10.
- **A bounded nomination latency floor remains, by choice.** Because nomination waits for higher-priority pairs
  to resolve, an unreachable high-priority pair still adds its (now bounded, ~2 s worst-case) transaction budget
  before a lower validated pair is nominated. This is marked `// DECISION` at the gate in `IceNominationDriver`.
- **Hot-path hardening.** The pair's send path and target are snapshotted under the driver lock before each
  check, so a concurrent priority upgrade of a struct `IceRemoteCandidate` cannot tear the read.

## Alternatives considered

- **Aggressive nomination (RFC 8445 §8.1.1, last paragraph)** — nominate the first validated pair immediately,
  carrying USE-CANDIDATE on the ordinary check. Lower setup latency, but it can lock onto a suboptimal pair
  (a working relay adopted before a better direct pair is even checked) and offers no clean re-selection under
  regular nomination semantics. Rejected for now; the paced overlap already removes most of the serial latency
  while keeping pair optimality. Revisit if field data shows the nomination floor dominates real setup time.
- **Keep the serial loop, only add retransmission.** Fixes the loss case but not the head-of-line blocking of a
  reachable pair behind an unreachable higher-priority one. Insufficient.
