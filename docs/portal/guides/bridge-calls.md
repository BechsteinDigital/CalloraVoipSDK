# Bridge two calls

A **bridge** cross-connects the audio of two active calls — the basis for attended
transfer, conferencing and "connect caller A to agent B" flows. Use a `MediaConnector`
from the media factory.

## Connect two calls

```csharp
// Two independent, connected calls:
ICall a = await line.DialAsync("sip:agent@pbx.example.com");
ICall b = await line.DialAsync("sip:customer@pbx.example.com");
// …both answered…

MediaConnector connector = client.Media.CreateConnector();
// cross-connect a <-> b through the connector, then tear down when done
```

Both calls keep their own SIP dialog; only their media is joined. Hanging up one leg
leaves the other intact.

## Bridge audio format

`VoipConfiguration.BridgeAudioFormat` controls how bridged audio is handled:

- `Passthrough` (default) — forward frames without re-encoding when the legs are
  compatible (lowest overhead).
- transcoding formats — mix/convert when the two legs negotiated different codecs.

```csharp
new VoipConfiguration { BridgeAudioFormat = BridgeAudioFormat.Passthrough };
```

## Conference recording / playback

Once bridged, record or play into the conference rather than a single call:

```csharp
await client.Media.StartConferenceRecordingAsync(/* … */);
await client.Media.StartConferencePlaybackAsync(/* … */);
```

See [Recording/playback](recording-playback.md).

## Attended transfer vs. bridge

`ICall.AttendedTransferAsync` hands the dialog to the PBX and drops out. A
`MediaConnector` bridge keeps **you** in the media path — pick the bridge when you need
to stay connected (supervision, recording, injecting audio).
