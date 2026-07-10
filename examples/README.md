# CalloraVoipSdk — Examples

Runnable console samples for the SDK. Each is a standalone project referencing the SDK
via `ProjectReference` and is part of `CalloraVoipSdk.sln`.

| Sample | Shows | Docs |
|--------|-------|------|
| [BasicCalling](CalloraVoipSdk.Sample.BasicCalling) | Register, place/receive a call, default audio, interactive control | [Getting Started](../docs/portal/getting-started/outbound-call.md) |
| [Dialer](CalloraVoipSdk.Sample.Dialer) | Sequential campaign dialing over a list of targets | [Making calls](../docs/portal/guides/making-calls.md) |
| [Transfer](CalloraVoipSdk.Sample.Transfer) | Blind and attended transfer | [Making calls](../docs/portal/guides/making-calls.md) |
| [CustomAudio](CalloraVoipSdk.Sample.CustomAudio) | Media tap: receive frame stats + inject a generated PCMU tone (no audio hardware) | [Media tap](../docs/portal/guides/media-tap.md) |

## Run

```bash
dotnet run --project examples/CalloraVoipSdk.Sample.BasicCalling
# samples with arguments:
dotnet run --project examples/CalloraVoipSdk.Sample.Dialer -- <server> <user> <password> <target1> [target2 ...]
dotnet run --project examples/CalloraVoipSdk.Sample.Transfer -- <server> <user> <password> [target-A]
dotnet run --project examples/CalloraVoipSdk.Sample.CustomAudio -- <server> <user> <password> <target>
```

All samples target `net8.0` and use a real SIP server/PBX — point them at your own
extension or trunk credentials.

## Commercial examples

[`commercial/`](commercial) holds samples for the paid modules (Conference, Realtime,
WebSocket). They depend on modules that are **not** part of the open SDK core and are
therefore excluded from the solution build — see [commercial/README.md](commercial/README.md).
