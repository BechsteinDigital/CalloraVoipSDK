using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Domain configuration is validated where it is written, not where it eventually breaks (#165 P3-11). A blank
/// registrar host, a port outside 1..65535, a non-positive registration lifetime or a backoff window that
/// cannot grow were all accepted and only surfaced later — as a socket error with no hint at which account
/// caused it, as a registration that expires the moment it is granted, or as retry timing nobody asked for.
/// The value objects next door (<c>SipCredentials</c>, <c>SipAddress</c>) already validated on construction;
/// this extends the same rule to the configuration objects.
/// </summary>
public sealed class DomainConfigurationValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_registrar_host_is_rejected(string host)
        => Assert.Throws<ArgumentException>(() => new SipAccount { Username = "u", SipServer = host });

    [Theory]
    [InlineData(-1)]
    [InlineData(70000)]
    public void A_port_outside_the_valid_range_is_rejected(int port)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new SipAccount { Username = "u", SipServer = "s", Port = port });

    [Fact]
    public void Port_zero_still_means_the_transport_default()
    {
        var account = new SipAccount { Username = "u", SipServer = "s", Port = 0 };

        Assert.Equal(0, account.Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void A_non_positive_registration_lifetime_is_rejected(int expiry)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new SipAccount { Username = "u", SipServer = "s", RegistrationExpiry = expiry });

    [Fact]
    public void A_blank_outbound_proxy_or_public_host_is_rejected_while_null_stays_allowed()
    {
        Assert.Throws<ArgumentException>(
            () => new SipAccount { Username = "u", SipServer = "s", OutboundProxy = " " });
        Assert.Throws<ArgumentException>(
            () => new SipAccount { Username = "u", SipServer = "s", PublicSipHost = " " });

        var account = new SipAccount { Username = "u", SipServer = "s" };
        Assert.Null(account.OutboundProxy);
        Assert.Null(account.PublicSipHost);
    }

    [Fact]
    public void Negative_retry_counts_and_non_positive_delays_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReregisterOptions { MaxRetries = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReregisterOptions { InitialRetryDelay = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReregisterOptions { MaxRetryDelay = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReregisterOptions { MinRefreshInterval = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReregisterOptions { MaxCorrectiveReregistrations = -1 });
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(1.5d)]
    public void A_refresh_ratio_outside_the_open_unit_interval_is_rejected(double ratio)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ReregisterOptions { RefreshRatio = ratio });

    [Fact]
    public void A_backoff_ceiling_below_its_floor_is_rejected_when_the_line_is_built()
    {
        // Cross-property, so no single initialiser can see it: the check runs when the line is built.
        var account = new SipAccount
        {
            Username = "u",
            SipServer = "s",
            Reregister = new ReregisterOptions
            {
                InitialRetryDelay = TimeSpan.FromSeconds(30),
                MaxRetryDelay = TimeSpan.FromSeconds(5),
            },
        };

        var error = Assert.Throws<ArgumentException>(() => new PhoneLine(
            account, new InertLineChannel(), new CallManager(), maxCalls: 0, NullLoggerFactory.Instance));

        Assert.Contains("MaxRetryDelay", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_options_and_a_plain_account_still_build_a_line()
    {
        var account = new SipAccount { Username = "u", SipServer = "s" };

        var line = new PhoneLine(
            account, new InertLineChannel(), new CallManager(), maxCalls: 0, NullLoggerFactory.Instance);

        Assert.Same(account, line.Account);
    }

    private sealed class InertLineChannel : ILineChannel
    {
        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        { }

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();
        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) => throw new NotSupportedException();
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
