using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>
/// L3-Fixture: treibt den echten <see cref="SipLineChannel"/>-Registrierungs-Loop mit einem
/// kurzen Expires (schnelle Re-REGISTER-Zyklen) gegen einen aufzeichnenden Fake-Registrar.
/// Exponiert die beobachteten Zyklen und den erreichten Registrierungs-Zustand public.
/// </summary>
public sealed class SipRegisterLoopHarness : IAsyncDisposable
{
    private readonly SipLineChannel _channel;
    private readonly RecordingRegistrationService _registrar;
    private volatile bool _reachedRegistered;

    private SipRegisterLoopHarness(SipLineChannel channel, RecordingRegistrationService registrar)
    {
        _channel = channel;
        _registrar = registrar;
    }

    /// <summary>True, sobald der Loop mindestens einmal <see cref="LineState.Registered"/> meldete.</summary>
    public bool ReachedRegistered => _reachedRegistered;

    /// <summary>Startet den Registrierungs-Loop; <paramref name="effectiveExpiresSeconds"/> steuert die Zyklus-Rate.</summary>
    public static SipRegisterLoopHarness Start(int effectiveExpiresSeconds)
    {
        var registrar = new RecordingRegistrationService(effectiveExpiresSeconds);
        var channel = new SipLineChannel(
            new SipAccount { Username = "soak", Password = "p", SipServer = "sipconnect.example" },
            "InteropHarness/1.0",
            registrar,
            new NoopCallSignaling(),
            new NoopSdpNegotiatorStub(),
            iceAgent: null,
            SrtpPolicy.Optional,
            telemetry: null,
            NullLoggerFactory.Instance);

        var harness = new SipRegisterLoopHarness(channel, registrar);
        channel.StartRegistration(state =>
        {
            if (state == LineState.Registered) harness._reachedRegistered = true;
        });
        return harness;
    }

    /// <summary>Lässt den Loop <paramref name="duration"/> lang laufen und gibt die beobachteten Zyklen zurück.</summary>
    public async Task<IReadOnlyList<RegisterCycle>> RunAsync(TimeSpan duration)
    {
        await Task.Delay(duration);
        return _registrar.Cycles;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.StopRegistration();
        await Task.Delay(50);
        _channel.Dispose();
    }
}
