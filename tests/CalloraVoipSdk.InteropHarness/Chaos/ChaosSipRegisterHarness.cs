using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.InteropHarness.Signaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.InteropHarness.Chaos;

/// <summary>
/// Drives the real <see cref="SipLineChannel"/> registration loop against a
/// <see cref="FaultInjectingRegistrationService"/> whose reachability can be toggled at runtime. The CORE-011
/// signaling chaos test uses it to prove the loop survives a registrar outage (keeps retrying, no wedge) and
/// recovers when the registrar returns. Fast back-off/refresh timings keep the test snappy.
/// </summary>
public sealed class ChaosSipRegisterHarness : IAsyncDisposable
{
    private readonly SipLineChannel _channel;
    private readonly FaultInjectingRegistrationService _registrar;
    private long _registeredCount;

    private ChaosSipRegisterHarness(SipLineChannel channel, FaultInjectingRegistrationService registrar)
    {
        _channel = channel;
        _registrar = registrar;
    }

    /// <summary>How many times the line has reached <see cref="LineState.Registered"/>.</summary>
    public long RegisteredCount => Interlocked.Read(ref _registeredCount);

    /// <summary>REGISTER attempts the loop has made — grows while it retries under a fault.</summary>
    public long RegisterAttempts => _registrar.Attempts;

    /// <summary>Starts the registration loop; <paramref name="initiallyFaulting"/> makes the registrar start unreachable.</summary>
    public static ChaosSipRegisterHarness Start(bool initiallyFaulting, int effectiveExpiresSeconds = 1)
    {
        var registrar = new FaultInjectingRegistrationService(effectiveExpiresSeconds, initiallyFaulting);
        var channel = new SipLineChannel(
            new SipAccount
            {
                Username = "chaos",
                Password = "p",
                SipServer = "chaos.example",
                // Fast, unlimited retries + frequent refresh so the fault bites and heals within a test window.
                Reregister = new ReregisterOptions
                {
                    InitialRetryDelay = TimeSpan.FromMilliseconds(100),
                    MaxRetryDelay = TimeSpan.FromMilliseconds(200),
                    MinRefreshInterval = TimeSpan.FromMilliseconds(200),
                },
            },
            "InteropHarness/1.0",
            registrar,
            new NoopCallSignaling(),
            new NoopSdpNegotiatorStub(),
            iceAgent: null,
            SrtpPolicy.Optional,
            telemetry: null,
            NullLoggerFactory.Instance);

        var harness = new ChaosSipRegisterHarness(channel, registrar);
        channel.StartRegistration(state =>
        {
            if (state == LineState.Registered)
                Interlocked.Increment(ref harness._registeredCount);
        });
        return harness;
    }

    /// <summary>Toggles the registrar between reachable and unreachable.</summary>
    public void SetRegistrarFault(bool faulting) => _registrar.SetFault(faulting);

    /// <summary>
    /// Polls until <see cref="RegisteredCount"/> reaches <paramref name="target"/> or <paramref name="timeout"/>
    /// elapses; returns whether the target was reached.
    /// </summary>
    public async Task<bool> WaitForRegistrationsAsync(long target, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (RegisteredCount < target)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(25, cts.Token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return RegisteredCount >= target;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _channel.StopRegistrationAsync().ConfigureAwait(false);
        _channel.Dispose();
    }
}
