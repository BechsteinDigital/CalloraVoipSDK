# Demo Walkthrough

This walkthrough builds an interactive console app that demonstrates the full call
lifecycle against a real SIP server. Create a new console project, add the
[NuGet packages](../getting-started/install.md), and follow the sections
below — together they form a complete `Program.cs`.

```bash
dotnet new console -n BasicCallingDemo
cd BasicCallingDemo
dotnet add package CalloraVoipSdk
dotnet add package CalloraVoipSdk.Audio.Linux   # or .Windows
```

## What It Demonstrates

### 1. SDK Initialization

```csharp
using var client = new VoipClient(new SdkConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent     = "CalloraVoipSdk-Demo/1.0",
});
```

`SdkConfiguration` wires up the logger factory and user-agent header. Audio routing is done via convenience APIs (`AttachDefaultAudioAsync`) and stays additive to the manual media API.

### 2. Registration

```csharp
var connectResult = await client.ConnectAsync(
    account,
    new ConnectOptions
    {
        Timeout = TimeSpan.FromSeconds(15),
        FailFastOnRegistrationFailed = true
    });

if (!connectResult.IsSuccess || connectResult.Line is null)
    return 1;

var line = connectResult.Line;
```

After a successful `ConnectAsync`, the line is ready to place and receive calls.

### 3. Audio Device Binding

The demo uses convenience default audio attach/detach. On `CallState.Connected`
it calls `AttachDefaultAudioAsync`, and cleanup on `Terminated` happens automatically.

```csharp
if (e.NewState == CallState.Connected)
{
    activeCall = e.Call;
    _ = client.AttachDefaultAudioAsync(e.Call);
}
```

### 4. Interactive Call Control

Once a call is connected, the demo accepts keyboard input for live call control:

- **`d <target>`** — Dial a SIP URI (e.g., `d sip:100@192.168.1.1`)
- **`a`** — Accept an incoming call
- **`r`** — Reject an incoming call
- **`Enter` or `h`** — Hangup the active call
- **`q`** — Quit the demo

### 5. Incoming Call Handling

The demo auto-rejects incoming calls when already in a call (busy), otherwise prompts the user:

```csharp
line.IncomingCall += (_, e) =>
{
    if (activeCall is not null || pendingInbound is not null)
    {
        Console.WriteLine($"[Eingehend] Von: {e.Call.RemoteParty} — abgelehnt (besetzt).");
        _ = e.Call.HangupAsync();
        return;
    }
    
    pendingInbound = e.Call;
    Console.WriteLine($"[Eingehend] Von: {e.Call.RemoteParty}");
    Console.WriteLine("  a = annehmen   r = ablehnen");
};
```

## Cleanup

On exit, the demo gracefully:
1. Hangs up any active or pending call
2. Unregisters the line from the SIP server
3. Disposes `VoipClient` (including convenience audio/session resources)
