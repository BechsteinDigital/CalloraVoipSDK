# Getting Started

## Prerequisites

- .NET SDK 8.0 or later
- A SIP account / PBX server (e.g. Asterisk, FreePBX, 3CX)

## Installation

Install from [nuget.org](https://www.nuget.org/packages/CalloraVoipSdk):

```bash
dotnet add package CalloraVoipSdk
dotnet add package CalloraVoipSdk.Audio.Linux     # Linux audio I/O
# or
dotnet add package CalloraVoipSdk.Audio.Windows   # Windows audio I/O
```

For local development against a checkout, `ProjectReference` works as well:

```xml
<ItemGroup>
  <ProjectReference Include="..\voip\src\Client\CalloraVoipSdk.Client.csproj" />
  <ProjectReference Include="..\voip\src\Core\CalloraVoipSdk.Core.csproj" />
  <ProjectReference Include="..\voip\src\Audio\Linux\CalloraVoipSdk.Audio.Linux.csproj" />
</ItemGroup>
```

## Create VoipClient

`VoipClient` is the single entry point for all SDK operations.

```csharp
using Microsoft.Extensions.Logging;
using CalloraVoipSdk;

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));

using var client = new VoipClient(new SdkConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent = "MyApp/1.0",
    MaxConcurrentCallsPerLine = 4
});
```

## Register a SIP Line (Convenience)

```csharp
using CalloraVoipSdk.Core.Domain.Lines;

var connectResult = await client.ConnectAsync(
    new SipAccount
    {
        Username    = "1001",
        Password    = "secret",
        SipServer   = "pbx.example.com",
        DisplayName = "Agent 1001",
        Transport   = SipTransport.Tls
    },
    new ConnectOptions
    {
        Timeout = TimeSpan.FromSeconds(15),
        FailFastOnRegistrationFailed = true
    });

if (!connectResult.IsSuccess || connectResult.Line is null)
    throw new InvalidOperationException($"Connect failed: {connectResult.Status}");

var line = connectResult.Line;
```

## Register a SIP Line (Advanced Event-Driven Flow)

```csharp
var line = client.Lines.Register(account);
line.StateChanged += (_, e) =>
    Console.WriteLine($"Line state: {e.NewState}");
```

Both flows are supported. Convenience is additive; the event-driven low-level flow remains unchanged.

## Cleanup

Always dispose `VoipClient` at shutdown — it tears down registrations and media sessions cleanly:

```csharp
await client.Lines.UnregisterAsync(line.LineId);
// VoipClient.Dispose() is called automatically via `using`
```

## Next Step

→ [Making Calls](making-calls.md)
