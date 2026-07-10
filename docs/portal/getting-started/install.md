# Install from NuGet

CalloraVoipSdk targets **net8.0**, **net9.0** and **net10.0**. The core SDK and the
platform audio backends ship as separate packages so you only pull in what you need.

## Packages

| Package | Purpose |
|---------|---------|
| `CalloraVoipSdk.Core` | SIP/RTP/SRTP/SDP stack and the media pipeline |
| `CalloraVoipSdk.Client` | The public `VoipClient` facade |
| `CalloraVoipSdk.Audio.Linux` | ALSA/PulseAudio device backend for Linux |
| `CalloraVoipSdk.Audio.Windows` | WASAPI device backend for Windows |

```bash
dotnet add package CalloraVoipSdk.Client
# plus the audio backend for your host OS:
dotnet add package CalloraVoipSdk.Audio.Linux
# or
dotnet add package CalloraVoipSdk.Audio.Windows
```

You can run headless without an audio backend — media then flows through frame
receivers/senders (see the [Media tap guide](../guides/media-tap.md)) or a silence device.

## First `VoipClient`

The client is the single entry point. Construct it with an optional
[`SdkConfiguration`](../concepts/voipclient.md) and dispose it when you shut down.

```csharp
using CalloraVoipSdk.Client;

using var client = new VoipClient(new SdkConfiguration
{
    LoggerFactory = loggerFactory,   // optional; wire your ILoggerFactory for diagnostics
    UserAgent     = "MySoftphone/1.0"
});
```

`SrtpPolicy` defaults to `Optional` — the SDK answers and offers SRTP when the peer
supports it and falls back to plain RTP otherwise. See
[SRTP/SRTCP](../guides/srtp-srtcp.md) to require or disable encryption.

## Next steps

- [Minimal outbound call](outbound-call.md)
- [Minimal inbound call](inbound-call.md)
- [Core Concepts](../concepts/voipclient.md)
