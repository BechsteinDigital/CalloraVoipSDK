# Recording / playback

Record a call to disk or play a file into it. Both are `client.Media` operations that
return a controllable session.

## Record a call

```csharp
IRecordingSession rec = await client.Media.StartCallRecordingAsync(call, "call.wav");

await rec.PauseAsync();
await rec.ResumeAsync();
await rec.StopAsync();      // finalizes the file
```

`StartConferenceRecordingAsync` records a [bridged](bridge-calls.md) conference instead
of a single leg.

## Play a file into a call

```csharp
IPlaybackSession play = await client.Media.StartCallPlaybackAsync(call, "prompt.wav");

await play.PauseAsync();
await play.ResumeAsync();
await play.StopAsync();
```

`StartConferencePlaybackAsync` plays into a conference.

## Formats

WAV and MP3 are supported for playback; recording writes WAV. The session decodes/encodes
against the call's negotiated audio format for you.

> **MP3 caveat:** MP3 playback expects raw MPEG frames from byte 0. Files with an ID3v2 tag
> (the common case) or VBR headers currently fail rather than resynchronizing to the next
> frame. Strip ID3 tags first, or use WAV, until this is improved
> ([issue tracker](https://github.com/BechsteinDigital/callora-voip-sdk/issues)).

## Lifecycle notes

- Always `StopAsync` a recording to flush and close the file cleanly — a dropped session
  may leave the file unfinalized.
- Recording and playback compose with a [media tap](media-tap.md): you can record while
  a bot injects audio on the same call.
- Sessions are tied to the call; ending the call ends the session.
