using CalloraVoipSdk.Core.Application.Media.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies RFC 7675 consent-freshness: the pure timing policy (randomized interval, 30 s
/// expiry) and the monitor loop that raises consent loss when checks stop being answered and
/// stays quiet while they succeed.
/// </summary>
public sealed class IceConsentFreshnessTests
{
    // --- Policy (pure timing) ---

    [Theory]
    [InlineData(0.0, 4)]   // 0.8 * 5 s
    [InlineData(0.5, 5)]   // 1.0 * 5 s
    [InlineData(1.0, 6)]   // 1.2 * 5 s
    public void Next_check_delay_scales_base_interval_between_0_8_and_1_2(double random01, int expectedSeconds)
    {
        var policy = new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.NextCheckDelay(random01));
    }

    [Theory]
    [InlineData(-1.0, 4)]  // clamped to 0.8
    [InlineData(2.0, 6)]   // clamped to 1.2
    public void Next_check_delay_clamps_random_factor(double random01, int expectedSeconds)
    {
        var policy = new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.NextCheckDelay(random01));
    }

    [Fact]
    public void Consent_is_fresh_up_to_30_seconds_and_stale_after()
    {
        var policy = new IceConsentFreshnessPolicy();
        var t0 = DateTimeOffset.UnixEpoch;

        Assert.True(policy.IsConsentFresh(t0, t0 + TimeSpan.FromSeconds(29)));
        Assert.True(policy.IsConsentFresh(t0, t0 + TimeSpan.FromSeconds(30)));
        Assert.False(policy.IsConsentFresh(t0, t0 + TimeSpan.FromSeconds(31)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    public void Base_interval_must_be_positive_and_below_expiry(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(seconds)));
    }

    // --- Monitor (loop) ---

    [Fact]
    public async Task Monitor_raises_consent_lost_when_checks_fail_past_expiry()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var lost = new TaskCompletionSource();
        var checks = 0;

        await using var monitor = new IceConsentMonitor(
            new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5)),
            sendConsentCheck: _ =>
            {
                Interlocked.Increment(ref checks);
                clock.Advance(TimeSpan.FromSeconds(11)); // each unanswered check pushes time forward
                return Task.FromResult(false);
            },
            onConsentLost: () => lost.TrySetResult(),
            loggerFactory: NullLoggerFactory.Instance,
            utcNow: () => clock.Now,
            delay: (_, ct) => Task.Delay(1, ct),
            nextRandom: () => 0.5);

        monitor.Start();

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // After 3 unanswered checks the clock is 33 s past the last confirmation (> 30 s expiry).
        Assert.True(checks >= 3);
    }

    [Fact]
    public async Task Monitor_stays_fresh_while_checks_succeed_and_stops_on_dispose()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var lost = false;
        var threeChecks = new TaskCompletionSource();
        var checks = 0;

        var monitor = new IceConsentMonitor(
            new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5)),
            sendConsentCheck: _ =>
            {
                var n = Interlocked.Increment(ref checks);
                clock.Advance(TimeSpan.FromSeconds(1));
                if (n == 3)
                    threeChecks.TrySetResult();
                return Task.FromResult(true);
            },
            onConsentLost: () => lost = true,
            loggerFactory: NullLoggerFactory.Instance,
            utcNow: () => clock.Now,
            delay: (_, ct) => Task.Delay(1, ct),
            nextRandom: () => 0.5);

        monitor.Start();
        await threeChecks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.DisposeAsync();

        Assert.False(lost);
        Assert.True(checks >= 3);
    }

    [Fact]
    public async Task Monitor_signals_degraded_then_recovered_across_a_transient_miss()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var degraded = new TaskCompletionSource();
        var recovered = new TaskCompletionSource();
        var checks = 0;

        await using var monitor = new IceConsentMonitor(
            new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5)),
            sendConsentCheck: _ =>
            {
                var n = Interlocked.Increment(ref checks);
                clock.Advance(TimeSpan.FromSeconds(5)); // stays inside the 30 s consent window
                return Task.FromResult(n != 1);        // check #1 unanswered (degrade), then answered (recover)
            },
            onConsentLost: () => { },
            loggerFactory: NullLoggerFactory.Instance,
            utcNow: () => clock.Now,
            delay: (_, ct) => Task.Delay(1, ct),
            nextRandom: () => 0.5,
            onConnectivityDegraded: () => degraded.TrySetResult(),
            onConnectivityRecovered: () => recovered.TrySetResult());

        monitor.Start();

        await degraded.Task.WaitAsync(TimeSpan.FromSeconds(5));   // transient miss surfaced
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));  // next answer recovers it
    }

    [Fact]
    public async Task Monitor_dispose_is_idempotent()
    {
        var monitor = new IceConsentMonitor(
            new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5)),
            sendConsentCheck: _ => Task.FromResult(true),
            onConsentLost: () => { },
            loggerFactory: NullLoggerFactory.Instance,
            delay: (_, ct) => Task.Delay(1, ct));

        monitor.Start();
        await monitor.DisposeAsync();
        await monitor.DisposeAsync(); // second dispose must not throw
    }

    private sealed class MutableClock
    {
        private long _ticks;

        public MutableClock(DateTimeOffset start) => _ticks = start.UtcTicks;

        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
