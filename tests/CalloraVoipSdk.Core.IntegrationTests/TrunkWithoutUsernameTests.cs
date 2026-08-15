using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #104 — an IP-authenticated trunk may have no account user at all, so <see cref="SipAccount.Username"/>
/// is no longer <c>required</c>. That removes the exact 1:1 inbound match, and the only rule left in
/// <c>TrunkInboundMatcher</c> is "anything addressed to our domain" — a line would answer calls meant for a
/// sibling line on the same provider domain. So an account without a username must bring an
/// <see cref="SipAccount.InboundNumbers"/> whitelist, and is refused otherwise rather than silently
/// over-accepting.
/// </summary>
public sealed class TrunkWithoutUsernameTests
{
    private static SipLineChannel NewChannel(SipAccount account) =>
        new(
            account,
            "test/1.0",
            new NoopRegistrationService(),
            new NoopSignalingService(),
            new NoopSdpNegotiator(),
            iceAgent: null,
            SrtpPolicy.Optional,
            telemetry: null,
            NullLoggerFactory.Instance);

    [Fact]
    public void An_account_without_a_username_needs_inbound_numbers()
    {
        var error = Assert.Throws<ArgumentException>(() => NewChannel(new SipAccount
        {
            SipServer = "trunk.example",
            Register = false,
        }));

        Assert.Contains("InboundNumbers", error.Message, StringComparison.Ordinal);
        Assert.Contains("trunk.example", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_without_a_username_but_with_inbound_numbers_is_accepted()
    {
        using var channel = NewChannel(new SipAccount
        {
            SipServer = "trunk.example",
            Register = false,
            InboundNumbers = ["4930123456"],
        });

        Assert.NotNull(channel);
    }

    [Fact]
    public void An_empty_inbound_number_list_does_not_count()
    {
        // A whitelist that whitelists nothing gives back no discrimination at all.
        Assert.Throws<ArgumentException>(() => NewChannel(new SipAccount
        {
            SipServer = "trunk.example",
            Register = false,
            InboundNumbers = [],
        }));
    }

    [Fact]
    public void A_username_alone_is_still_enough()
    {
        // The overwhelmingly common case must stay untouched: a username gives the 1:1 match, so no
        // whitelist is needed.
        using var channel = NewChannel(new SipAccount
        {
            Username = "alice",
            SipServer = "example.com",
        });

        Assert.NotNull(channel);
    }

    [Fact]
    public void The_rule_applies_to_registering_accounts_too()
    {
        // Not tied to Register = false: a registering account with no username has the same inbound
        // ambiguity, and a registrar would have nothing to bind either.
        Assert.Throws<ArgumentException>(() => NewChannel(new SipAccount { SipServer = "example.com" }));
    }

    private sealed class NoopRegistrationService : ISipRegistrationService
    {
        public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopSignalingService : ISipCallSignalingService
    {
        public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }
        public event EventHandler<SipIncomingMessageEventArgs>? IncomingMessage { add { } remove { } }
        public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted { add { } remove { } }

        public Task<ISipCallSession> InviteAsync(SipInviteRequest request, Action<ISipCallSession>? onSessionCreated = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> SendMessageAsync(SipMessageRequest request, CancellationToken ct = default) => Task.FromResult(200);

        public Task<SipPublishResult> PublishAsync(SipPublishRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SipPublishResult(200, null, 0));

        public void Dispose() { }
    }

    private sealed class NoopSdpNegotiator : ISdpNegotiator
    {
        public string BuildDefaultSdp(IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? options = null) => "v=0";
        public string? TryBuildNegotiatedAnswer(string remoteOffer, IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? localOptions = null) => null;
        public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint) => null;
        public bool IsRemoteHoldSdp(string? sdp) => false;
    }
}
