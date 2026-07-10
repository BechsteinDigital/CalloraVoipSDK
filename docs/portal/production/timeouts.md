# Timeouts

Real networks stall. These are the timeouts and guards the SDK exposes so a stalled call
does not leak resources.

## Inbound media timeout

```csharp
new SdkConfiguration { InboundMediaTimeout = TimeSpan.FromSeconds(15) };  // default
```

An answered inbound call that never produces media is torn down after this window — it
guards against half-open calls that consume a slot but carry no audio.

## Hold-silence hangup

```csharp
new SdkConfiguration { HangupHeldCallOnMediaSilence = true };
```

Ends held calls that go silent, so a forgotten hold doesn't linger indefinitely.

## Concurrency cap

```csharp
new SdkConfiguration { MaxConcurrentCallsPerLine = 10 };  // default
```

Bounds how many simultaneous calls a single line will carry — a backstop against runaway
fan-out.

## Registration expiry

```csharp
new SipAccount { RegistrationExpiry = 300 };  // seconds, default
```

The REGISTER `Expires`. The SDK refreshes before expiry; lower values detect network loss
sooner at the cost of more REGISTER traffic.

## Your own deadlines

Pass a `CancellationToken` to async calls to bound them from the caller side:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var dial = await client.DialAndWaitUntilConnectedAsync(line, uri, cts.Token);
```

Combine SDK-side guards (media/hold/concurrency) with caller-side cancellation for both
protocol-level and application-level deadlines.
