# Changelog

The authoritative changelog lives in the repository:
[`CHANGELOG.md`](https://github.com/BechsteinDigital/CalloraVoipSDK/blob/main/CHANGELOG.md)

## Release highlights

### 3.1.1 — 2026-07-08
- RFC 3550 jitter estimator fixed (arrival-time overflow made jitter converge to the
  frame interval); RTT measured from RTCP LSR/DLSR now feeds the adaptive jitter buffer

### 3.1.0 — 2026-07-08
Hardening from the first real-world interop test (AVM Fritz!Box, AI voice agent):
- `SdkConfiguration.PreferredAudioCodecs` — ordered codec preference for offers,
  answers and RTP sessions
- Advertised media address resolution fixed (no loopback towards LAN peers)
- Static payload types without rtpmap now negotiate correctly
- Reliable provisionals (RFC 3262) only on explicit `Require: 100rel`
- RTCP compound decoding tolerates unknown packet types (e.g. RFC 3611 XR)
- SIP wire trace diagnostics (Trace level, includes SDP bodies)

### 3.0.0 — 2026-07-07
- Module registry as the SDK extension point: `IVoipClientModule`, `client.Modules`,
  typed resolution via `Get<T>`/`TryGet<T>`
- Per-call media tap pinned as a public, tested contract

### 2.0.0 — 2026-07-07
- **Breaking:** unimplemented module facades removed from the public surface —
  these capabilities return as separate commercial plugins
- `net9.0` and `net10.0` target frameworks added
- First releases published to nuget.org
