using System.Net;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the media consent session (RFC 7675) built on the transaction registry and consent
/// monitor: it sends consent checks through the media send delegate and confirms them when the peer
/// answers (fed via <see cref="IceMediaConsentSession.OnStunResponse"/>), staying fresh; when checks
/// go unanswered past the consent lifetime it raises consent loss. The clock and delay are injected
/// for deterministic, fast execution.
/// </summary>
public sealed class IceMediaConsentSessionTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 5000);

    [Fact]
    public async Task Stays_fresh_while_the_peer_answers_consent_checks()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var lost = false;
        var threeChecks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        IceMediaConsentSession? session = null;

        ValueTask SendRaw(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref checks);
            // Answer the check: echo its transaction id (bytes 8..20) so OnStunResponse matches it.
            var response = new byte[20];
            datagram.Span.Slice(8, 12).CopyTo(response.AsSpan(8));
            session!.OnStunResponse(response);
            if (n == 3)
                threeChecks.TrySetResult();
            return ValueTask.CompletedTask;
        }

        session = new IceMediaConsentSession(
            new StunMessageCodec(), SendRaw, Remote,
            localUfrag: "localU", remoteUfrag: "peerU", remotePassword: "peerPwd",
            priority: 1u, controlling: true, tieBreaker: 1,
            onConsentLost: () => lost = true,
            loggerFactory: NullLoggerFactory.Instance,
            policy: new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5)),
            checkTimeout: TimeSpan.FromSeconds(1),
            utcNow: () => clock.Now,
            delay: (_, ct) => { clock.Advance(TimeSpan.FromSeconds(1)); return Task.Delay(1, ct); },
            nextRandom: () => 0.5);

        session.Start();
        await threeChecks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();

        Assert.False(lost);
        Assert.True(checks >= 3);
    }

    [Fact]
    public async Task Raises_consent_lost_when_checks_go_unanswered_past_expiry()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;

        await using var session = new IceMediaConsentSession(
            new StunMessageCodec(),
            sendRaw: (_, _, _) => { Interlocked.Increment(ref checks); return ValueTask.CompletedTask; },
            Remote,
            localUfrag: "localU", remoteUfrag: "peerU", remotePassword: "peerPwd",
            priority: 1u, controlling: true, tieBreaker: 1,
            onConsentLost: () => lost.TrySetResult(),
            loggerFactory: NullLoggerFactory.Instance,
            policy: new IceConsentFreshnessPolicy(TimeSpan.FromSeconds(5)),
            checkTimeout: TimeSpan.FromMilliseconds(30),
            utcNow: () => clock.Now,
            delay: (_, ct) => { clock.Advance(TimeSpan.FromSeconds(11)); return Task.Delay(1, ct); },
            nextRandom: () => 0.5);

        session.Start();

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(checks >= 3);
    }

    private sealed class MutableClock
    {
        private long _ticks;
        public MutableClock(DateTimeOffset start) => _ticks = start.UtcTicks;
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
