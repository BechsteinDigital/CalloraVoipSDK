# Logging

The SDK logs through `Microsoft.Extensions.Logging`. Wire an `ILoggerFactory` and you get
structured logs across signaling, media and registration.

## Enable logging

```csharp
using var client = new VoipClient(new SdkConfiguration
{
    LoggerFactory = loggerFactory   // your ILoggerFactory
});
```

Without a factory the SDK runs silently (no logging).

## Levels

| Level | What you get |
|-------|--------------|
| `Information` | Lifecycle milestones: register, dial, answer, hangup |
| `Warning` | Recoverable issues: retries, best-effort teardown failures, fallbacks |
| `Error` | Failures in critical paths |
| `Debug` | Detailed state transitions |
| `Trace` | Full SIP wire trace **including SDP bodies**, with SDES keys and ICE passwords redacted (see [Diagnostics](diagnostics.md)) |

`Trace` is verbose and prints signaling payloads — enable it for a targeted diagnosis,
not in steady-state production. SDES SRTP keys and ICE passwords are masked, but other
bodies are printed verbatim, so still treat trace logs as sensitive.

## What the SDK guarantees

- **No silent catch** in critical paths — swallowed exceptions are logged with context.
- Best-effort operations that fail (e.g. a BYE during dispose) are logged as warnings, not
  dropped.

## Recommendations

- Log at `Information` in production; raise to `Debug`/`Trace` per-category when
  investigating.
- Scope logs by call/line so concurrent calls stay separable — the SDK includes call/line
  identifiers in its log state.
- Route to structured sinks (Serilog, OpenTelemetry) via your `ILoggerFactory`.
