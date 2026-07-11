# Audio devices

The SDK plays and captures audio through a platform backend
(`CalloraVoipSdk.Audio.Linux` or `CalloraVoipSdk.Audio.Windows`). The `VoipClient`
exposes enumeration, hot-switch, mute and volume at runtime.

## Attach the default device

```csharp
await client.AttachDefaultAudioAsync(call);
await client.DetachDefaultAudioAsync(call);
```

With `EnableAutomaticAudioDeviceSelection` (default `true`) the SDK picks the OS default
input/output. Run headless by leaving the device unattached and using a
[media tap](media-tap.md) instead.

## Enumerate devices

```csharp
IReadOnlyList<AudioDeviceDescriptor> inputs  = client.GetAvailableInputAudioDevices();
IReadOnlyList<AudioDeviceDescriptor> outputs = client.GetAvailableOutputAudioDevices();
AudioDeviceRuntimeSnapshot snapshot          = client.GetAudioDeviceRuntimeSnapshot();
```

The snapshot reports the currently selected devices, mute and volume state.

## Hot-switch, mute, volume

These take effect on active calls without renegotiation:

```csharp
client.SwitchAudioInputDevice(deviceId);
client.SwitchAudioOutputDevice(deviceId);

client.SetAudioInputVolume(0.8f);    // 0.0 – 1.0
client.SetAudioOutputVolume(1.0f);

client.SetAudioInputMuted(true);
client.SetAudioOutputMuted(false);

client.UpdateAudioFormat(new AudioDeviceFormat(/* sample rate, channels … */));
```

Passing `null` to `SwitchAudioInputDevice`/`SwitchAudioOutputDevice` reverts to the OS
default.

## Codec preference

Wire format is chosen by SDP negotiation. Bias it with an ordered list once at
construction:

```csharp
new SdkConfiguration
{
    PreferredAudioCodecs = new[] { "PCMU", "PCMA", "G722" }
};
```

> **Codec support:** the platform audio backends (`Audio.Linux` / `Audio.Windows`) decode
> and encode **PCMU, PCMA and G.722** only. **Opus is not yet transcoded by the device
> backends** — a call that negotiates Opus will not play correctly through the default audio
> device. For Opus, drive the call through a [media tap](media-tap.md) with your own Opus
> codec instead of `AttachDefaultAudioAsync`.
