using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using SipTx = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Opt-in RFC 4568 §7 enforcement (F007 follow-up): with RequireSecureSignalingForSdes set, an outbound
/// call that would key SDES over an insecure signaling transport is refused fail-closed; over TLS/SIPS,
/// or without the flag, it proceeds (the flag-off path only warns).
/// </summary>
public sealed class SipLineChannelSdesEnforcementTests
{
    private static SipLineChannel Channel(SrtpPolicy policy, bool require, SipTx transport) =>
        new(new SipAccount { Username = "u", Password = "p", SipServer = "s", Transport = transport },
            "test/1.0", new NoopReg(), new NoopSig(), new NoopSdp(), iceAgent: null,
            policy, telemetry: null, NullLoggerFactory.Instance,
            preferredCodecNames: null, dtlsOptions: null, offerDtlsSrtp: false,
            enableVideo: false, preferredVideoCodecNames: null, requireSecureSignalingForSdes: require);

    [Fact]
    public void Require_over_insecure_udp_refuses_the_outbound_sdes_call()
    {
        using var line = Channel(SrtpPolicy.Optional, require: true, SipTx.Udp);
        Assert.Throws<InvalidOperationException>(() => line.PrepareOutboundChannel(DialOptions.Default));
    }

    [Fact]
    public void Require_over_secure_tls_allows_the_call()
    {
        using var line = Channel(SrtpPolicy.Optional, require: true, SipTx.Tls);
        var channel = line.PrepareOutboundChannel(DialOptions.Default); // TLS signaling protects the key
        Assert.NotNull(channel);
        (channel as IDisposable)?.Dispose();
    }

    [Fact]
    public void Without_the_flag_over_udp_the_call_proceeds()
    {
        using var line = Channel(SrtpPolicy.Optional, require: false, SipTx.Udp);
        var channel = line.PrepareOutboundChannel(DialOptions.Default); // warns, does not refuse
        Assert.NotNull(channel);
        (channel as IDisposable)?.Dispose();
    }

    [Fact]
    public void Require_with_srtp_disabled_proceeds_no_sdes_key_offered()
    {
        using var line = Channel(SrtpPolicy.Disabled, require: true, SipTx.Udp);
        var channel = line.PrepareOutboundChannel(DialOptions.Default); // plain RTP → no key exposed
        Assert.NotNull(channel);
        (channel as IDisposable)?.Dispose();
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class NoopReg : ISipRegistrationService
    {
        public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoopSig : ISipCallSignalingService
    {
        public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }
        public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted { add { } remove { } }
        public Task<ISipCallSession> InviteAsync(SipInviteRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class NoopSdp : ISdpNegotiator
    {
        public string BuildDefaultSdp(IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? options = null) => "v=0";
        public string? TryBuildNegotiatedAnswer(string remoteOffer, IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? localOptions = null) => null;
        public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint) => null;
        public bool IsRemoteHoldSdp(string? sdp) => false;
    }
}
