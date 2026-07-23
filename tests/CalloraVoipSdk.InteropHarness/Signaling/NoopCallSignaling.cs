using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.InteropHarness.Signaling;

/// <summary>Fake-Signaling-Service; im REGISTER-Soak nie aufgerufen.</summary>
internal sealed class NoopCallSignaling : ISipCallSignalingService
{
    /// <inheritdoc />
    public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }

    /// <inheritdoc />
    public event EventHandler<SipIncomingMessageEventArgs>? IncomingMessage { add { } remove { } }

    /// <inheritdoc />
    public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted { add { } remove { } }

    /// <inheritdoc />
    public Task<ISipCallSession> InviteAsync(SipInviteRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<int> SendMessageAsync(SipMessageRequest request, CancellationToken ct = default) => Task.FromResult(200);

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>Fake-SDP-Negotiator; im REGISTER-Soak nie aufgerufen.</summary>
internal sealed class NoopSdpNegotiatorStub : ISdpNegotiator
{
    /// <inheritdoc />
    public string BuildDefaultSdp(IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? options = null) => "v=0";

    /// <inheritdoc />
    public string? TryBuildNegotiatedAnswer(string remoteOffer, IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? localOptions = null) => null;

    /// <inheritdoc />
    public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint) => null;

    /// <inheritdoc />
    public bool IsRemoteHoldSdp(string? sdp) => false;
}
