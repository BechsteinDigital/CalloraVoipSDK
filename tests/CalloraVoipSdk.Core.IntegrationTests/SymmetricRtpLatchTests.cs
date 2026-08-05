using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14 #1: the symmetric-RTP latch hardened against the CVE-2017-14099 media-hijack pattern. The first
/// validated source always latches; a change of source re-latches only on a keyed (authenticated) call — a
/// plaintext call locks onto the first source so an unauthenticated flood cannot redirect outbound media.
/// </summary>
public sealed class SymmetricRtpLatchTests
{
    private static readonly IPEndPoint Fallback = new(IPAddress.Parse("10.0.0.1"), 5000);   // SDP-advertised
    private static readonly IPEndPoint PeerA = new(IPAddress.Parse("203.0.113.7"), 40000);  // real peer source
    private static readonly IPEndPoint AttackerB = new(IPAddress.Parse("198.51.100.9"), 40000);

    [Fact]
    public void Before_any_packet_the_target_is_the_fallback()
        => Assert.Equal(Fallback, new SymmetricRtpLatch(NullLogger.Instance).Target(Fallback));

    [Fact]
    public void The_first_validated_source_latches()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);

        latch.Consider(PeerA, authenticated: false); // first source always latches, even plaintext

        Assert.Equal(PeerA, latch.Target(Fallback));
    }

    [Fact]
    public void A_keyed_call_re_latches_to_a_new_source_for_a_nat_rebind()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: true);

        latch.Consider(AttackerB, authenticated: true); // authenticated → can only be the peer behind a rebind

        Assert.Equal(AttackerB, latch.Target(Fallback));
    }

    [Fact]
    public void A_plaintext_call_locks_and_refuses_a_new_source()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: false);

        latch.Consider(AttackerB, authenticated: false); // unauthenticated flood must not hijack outbound media

        Assert.Equal(PeerA, latch.Target(Fallback)); // media stays with the original peer
    }

    [Fact]
    public void The_same_source_never_changes_the_latch()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: false);
        latch.Consider(PeerA, authenticated: false);

        Assert.Equal(PeerA, latch.Target(Fallback));
    }

    // ── admission decision (#161 P1-4): the return value gates delivery, not just the outbound path ──

    [Fact]
    public void The_first_source_is_admitted()
        => Assert.True(new SymmetricRtpLatch(NullLogger.Instance).Consider(PeerA, authenticated: false));

    [Fact]
    public void The_latched_source_is_admitted()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: false);

        Assert.True(latch.Consider(PeerA, authenticated: false));
    }

    [Fact]
    public void A_plaintext_new_source_is_refused_for_delivery()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: false);

        // Refused: the caller must drop the packet, not just keep the outbound path latched.
        Assert.False(latch.Consider(AttackerB, authenticated: false));
        Assert.Equal(PeerA, latch.Target(Fallback));
    }

    [Fact]
    public void A_keyed_new_source_is_admitted_and_re_latches()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: true);

        Assert.True(latch.Consider(AttackerB, authenticated: true));
        Assert.Equal(AttackerB, latch.Target(Fallback));
    }

    // ── plain-RTCP source binding (#161 P1-4 B): control is bound to the latched media source ──

    [Fact]
    public void Control_is_admitted_before_any_source_has_latched()
        => Assert.True(new SymmetricRtpLatch(NullLogger.Instance).AdmitsControl(PeerA));

    [Fact]
    public void Control_from_the_latched_source_is_admitted()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: false);

        Assert.True(latch.AdmitsControl(PeerA));
    }

    [Fact]
    public void Control_from_a_foreign_source_is_refused()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: false);

        Assert.False(latch.AdmitsControl(AttackerB)); // spoofed feedback from a third party is not honoured
    }

    [Fact]
    public void Observing_control_does_not_establish_the_latch()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);

        latch.AdmitsControl(PeerA); // only validated RTP may latch — RTCP must never set the media source

        Assert.Equal(Fallback, latch.Target(Fallback)); // still unlatched
        Assert.True(latch.AdmitsControl(AttackerB));     // so a different control source is still admitted
    }

    [Fact]
    public void A_refused_control_source_is_logged_as_a_warning_once_per_source()
    {
        var logger = new CapturingLogger();
        var latch = new SymmetricRtpLatch(logger);
        latch.Consider(PeerA, authenticated: false);

        latch.AdmitsControl(AttackerB);
        latch.AdmitsControl(AttackerB); // same foreign source again → not logged twice

        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public void A_refused_re_latch_is_logged_as_a_warning_once_per_source()
    {
        var logger = new CapturingLogger();
        var latch = new SymmetricRtpLatch(logger);
        latch.Consider(PeerA, authenticated: false);

        latch.Consider(AttackerB, authenticated: false);
        latch.Consider(AttackerB, authenticated: false); // same attacker again → not logged twice

        Assert.Equal(1, logger.Warnings);
    }

    // ── collision gate (#161 P1-4 C): only the latched source is recognised as the media source ──

    [Fact]
    public void No_source_is_the_latched_source_before_a_latch()
        => Assert.False(new SymmetricRtpLatch(NullLogger.Instance).IsLatchedSource(PeerA));

    [Fact]
    public void The_latched_source_is_recognised()
    {
        var latch = new SymmetricRtpLatch(NullLogger.Instance);
        latch.Consider(PeerA, authenticated: false);

        Assert.True(latch.IsLatchedSource(PeerA));
        Assert.False(latch.IsLatchedSource(AttackerB));
    }

    private sealed class CapturingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings++;
        }
    }
}
