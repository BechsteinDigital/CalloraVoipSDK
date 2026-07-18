# Minimal inbound call

Register a line, accept the next incoming call, attach audio. Inbound calls surface
through the `IncomingCall` event (client- or line-level) or the convenience
`OnIncomingCall` hook.

```csharp
using CalloraVoipSdk.Client;

using var client = new VoipClient(new VoipConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent     = "MySoftphone/1.0"
});

// Register the account that should receive calls.
var connect = await client.ConnectAsync(new SipAccount
{
    Username  = "1001",
    Password  = "secret",
    SipServer = "pbx.example.com"
});

if (!connect.IsSuccess)
    throw new InvalidOperationException($"Connect failed: {connect.Status}");

// Handle each inbound call. The handler runs when an INVITE arrives.
using var subscription = client.OnIncomingCall(async call =>
{
    await call.AcceptAsync();                 // send 200 OK
    await client.AttachDefaultAudioAsync(call);
});

// Keep the process alive while calls come in…
```

## Accept, reject, redirect

Once you hold an `ICall`, the inbound control surface is:

```csharp
await call.AcceptAsync();                            // 200 OK, answer
await call.RejectAsync(/* reason */);                // decline with a SIP status
await call.RedirectAsync(/* target */);              // 3xx redirect to another URI
```

`AcceptAsync` throws `InvalidOperationException` if the call is not in a state that can
be accepted; `RejectAsync`/`RedirectAsync` return a `CallActionResult` instead of
throwing for foreseeable outcomes. See the [error contract](../production/threading.md#error-contract).

## Trunk vs. extension inbound

For SIP-trunk scenarios (calls addressed to DIDs rather than your registered AOR), set
the numbers you own on the account so the SDK matches inbound requests:

```csharp
new SipAccount
{
    Username       = "trunkuser",
    Password       = "secret",
    SipServer      = "trunk.provider.example",
    InboundNumbers = new[] { "4930123456" },
    AcceptTrunkInbound = true   // default
};
```

See [NAT and SIP trunks](../guides/nat-and-trunks.md) for provider-specific setup.

## Next

- [Handling inbound calls guide](../guides/inbound-calls.md)
- [Events concept](../concepts/events.md)
