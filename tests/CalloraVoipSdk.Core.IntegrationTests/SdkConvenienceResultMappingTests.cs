using CalloraVoipSdk.Core.Application.Convenience;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Convenience-layer dial result-mapping regressions (F008/F009): `DialAndWaitUntilConnectedAsync`
/// must map a ConnectTimeout-bounded ringing call to Timeout and a caller-cancelled dial to Canceled,
/// instead of the coarse Failed fallback. (F005, the register-side terminal-Failed short-circuit, is
/// covered end-to-end by the Asterisk interop suite; the register path returns a concrete PhoneLine and
/// is not fake-injectable here.)
/// </summary>
public sealed class SdkConvenienceResultMappingTests
{
    private static SdkConvenienceOrchestrator Orchestrator() =>
        new(new PhoneLineManager(_ => throw new NotSupportedException("lines are not registered here")),
            new MediaManager(), new NoopAudioDevice(), NullLoggerFactory.Instance, videoDevice: null);

    // F008: ConnectTimeout bounds the whole dial (here DialAsync blocks like a ringing peer) →
    // Timeout at the deadline, not Failed after the ring/transaction timeout.
    [Fact]
    public async Task DialAndWait_RingingPastConnectTimeout_YieldsTimeout()
    {
        var line = new FakeLine { Dial = async ct => { await Task.Delay(Timeout.Infinite, ct); return (ICall)null!; } };
        using var orchestrator = Orchestrator();

        var outcome = await orchestrator.DialAndWaitUntilConnectedAsync(
            line, "sip:x@h", dialOptions: null, TimeSpan.FromMilliseconds(200),
            hangupOnTimeout: false, hangupOnCancellation: false, CancellationToken.None);

        Assert.Equal(CallConnectStatus.Timeout, outcome.Status);
    }

    // F009: caller cancellation → Canceled even when the dial surfaces a non-OperationCanceledException
    // (as it does when the auto-CANCEL aborts the INVITE and a synthetic SIP failure is raised).
    [Fact]
    public async Task DialAndWait_CallerCancel_NonOceException_YieldsCanceled()
    {
        var line = new FakeLine
        {
            Dial = async ct =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { throw new InvalidOperationException("simulated CANCEL→408"); }
                return (ICall)null!;
            },
        };
        using var orchestrator = Orchestrator();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var outcome = await orchestrator.DialAndWaitUntilConnectedAsync(
            line, "sip:x@h", dialOptions: null, TimeSpan.FromSeconds(30),
            hangupOnTimeout: false, hangupOnCancellation: false, cts.Token);

        Assert.Equal(CallConnectStatus.Canceled, outcome.Status);
    }

    // Control: a genuine dial failure (non-cancel, non-timeout) still maps to Failed.
    [Fact]
    public async Task DialAndWait_GenuineFailure_YieldsFailed()
    {
        var line = new FakeLine { Dial = _ => throw new InvalidOperationException("peer rejected") };
        using var orchestrator = Orchestrator();

        var outcome = await orchestrator.DialAndWaitUntilConnectedAsync(
            line, "sip:x@h", dialOptions: null, TimeSpan.FromSeconds(30),
            hangupOnTimeout: false, hangupOnCancellation: false, CancellationToken.None);

        Assert.Equal(CallConnectStatus.Failed, outcome.Status);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeLine : IPhoneLine
    {
        public LineId LineId { get; } = LineId.New();
        public SipAccount Account { get; } = new() { Username = "u", SipServer = "s" };
        public LineState State { get; set; } = LineState.Registered;
        public Func<CancellationToken, Task<ICall>>? Dial { get; set; }

        public event EventHandler<LineStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<IncomingCallEventArgs>? IncomingCall { add { } remove { } }
        public event EventHandler<OutboundCallRingingEventArgs>? OutboundCallRinging { add { } remove { } }
        public event EventHandler<LineReconnectingEventArgs>? LineReconnecting { add { } remove { } }
        public event EventHandler<LineReconnectFailedEventArgs>? LineReconnectFailed { add { } remove { } }

        public Task<ICall> DialAsync(string targetUri, DialOptions? options = null, CancellationToken ct = default) => Dial!(ct);
        public Task UnregisterAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopAudioDevice : IAudioDevice
    {
        public string Name => "noop";
        public void Connect(IMediaReceiver receiver, IMediaSender sender, AudioConnectionParameters parameters) { }
        public void Disconnect() { }
    }
}
