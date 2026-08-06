using System.Runtime.CompilerServices;

// Core's internal machinery (the SIP/RTP/SRTP/STUN/TURN/ICE runtime, and the shared Opus codec) is
// deliberately NOT part of the public API surface: consumers program against the CalloraVoipSdk.Client
// facade (VoipClient), never the internal types. Keeping that machinery internal keeps the shipped
// consumer API small and free to evolve (the "minimal public surface — facade is the API" principle).
//
// The grants below are therefore an intentional, audited multi-assembly design (#17.14), not accidental
// sprawl. Each was verified to be load-bearing — removing it fails the build — and the alternatives were
// weighed and rejected:
//   * Making the shared types public would bloat the consumer API surface (the opposite of the goal).
//   * Duplicating them per assembly would fork the SIP/RTP/Opus implementations.
// So the internals stay internal and are shared, narrowly, only with these first-party assemblies:
//
//   - CalloraVoipSdk.Client — the public facade assembly; it composes the internal runtime into
//     VoipClient. That composition is its entire reason for existing.
//   - CalloraVoipSdk.Audio.Linux / .Windows — the shipped platform audio packages. They consume public
//     Core abstractions (IAudioDevice, the Audio.Abstractions helpers) AND the internal Opus codec
//     (OpusDeviceCodec -> OpusPayloadCodec), which is shared with Core's RTP media path rather than
//     duplicated into each audio backend.
//   - CalloraVoipSdk.Audio.Abstractions — the platform-neutral audio package. It wraps the internal
//     payload codecs (Opus/G.711/G.722) behind the public IAudioPayloadCodec transcoding surface (#205)
//     so server-side consumers (e.g. an SFU bridging a phone leg into a WebRTC conference) can decode
//     and re-encode PCM16 without binding Concentus themselves; the Concentus/NAudio types never leak.
//   - CalloraVoipSdk.InteropHarness + the *.Tests / *.Performance assemblies — test, benchmark and
//     interop-harness code that must exercise the internals directly; none are shipped to consumers.

// ── Test / benchmark / interop-harness assemblies ────────────────────────────────────────────────
[assembly: InternalsVisibleTo("CalloraVoipSdk.Tests")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Core.Tests")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Core.IntegrationTests")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.InteropTests")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Conferencing.Tests")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Performance")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Core.Performance")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.InteropHarness")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Client.Tests")]

// ── Shipped first-party assemblies (facade composition + shared Opus codec) ──────────────────────
[assembly: InternalsVisibleTo("CalloraVoipSdk.Client")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Audio.Abstractions")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Audio.Linux")]
[assembly: InternalsVisibleTo("CalloraVoipSdk.Audio.Windows")]
