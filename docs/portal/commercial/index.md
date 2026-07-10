# Commercial modules

> **Status: in development — not yet available.** The pages in this section describe
> planned commercial plugins and their intended integration surface. They are **not**
> shipping products yet. Nothing here is a support or availability commitment.

The SDK core is open and free. Advanced capabilities are planned as **paid plugins** on a
private feed, built on the two public extension points the core already exposes:

- the [module registry](../concepts/modules.md) (`client.Modules`, `Get<T>`/`TryGet<T>`)
- the [media-tap contract](../guides/media-tap.md) (`CreateReceiver`/`CreateSender`)

Because these are the same public contracts your own in-house modules can use, the plugin
model adds no private hooks — a commercial module is "just" a module that consumes the
tap.

## Planned modules

| Module | Intent |
|--------|--------|
| [Realtime](realtime.md) | Bridge call audio to realtime AI APIs (pacing, backpressure, barge-in) |
| [WebSocket](websocket.md) | Raw call-audio streaming over WebSocket |
| [Privacy](privacy.md) | Redaction, consent management, policy gates, audit trail |
| [Risk](risk.md) | Spam/scam signals, call risk screening, PBX abuse prevention |
| [Intelligence](intelligence.md) | AMD, sentiment, transcription, local model integration |

## Early access

Interested in early access or shaping the roadmap? Contact
[info@bechstein.digital](mailto:info@bechstein.digital).
