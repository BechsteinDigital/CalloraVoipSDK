using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins CORE-019 (inbound part): the parsed remote asserted identity (RFC 3325) and Diversion
/// (RFC 5806) surface read-only on <see cref="ICall"/>, and Diversion header parsing.
/// </summary>
public sealed class SipRemoteIdentityTests
{
    [Theory]
    [InlineData("<sip:orig@pbx.example>;reason=unconditional;counter=1", "sip:orig@pbx.example")]
    [InlineData("\"Reception\" <sip:reception@pbx.example>;reason=no-answer", "sip:reception@pbx.example")]
    [InlineData("sip:bob@pbx.example;reason=user-busy", "sip:bob@pbx.example")]
    public void ParseDiversionUri_extracts_the_uri(string header, string expected)
    {
        Assert.Equal(expected, SipCallSessionUtilities.ParseDiversionUri(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDiversionUri_returns_null_for_absent_header(string? header)
    {
        Assert.Null(SipCallSessionUtilities.ParseDiversionUri(header));
    }

    [Fact]
    public void Remote_identity_and_diversion_surface_on_the_call()
    {
        using var channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        var call = new Call(
            CallId.New(),
            CallDirection.Inbound,
            "sip:remote@test.invalid",
            channel,
            new FakePhoneLine(),
            NullLogger<Call>.Instance);

        // Before a session is attached the identity is unknown.
        Assert.Null(call.RemoteAssertedIdentity);
        Assert.Null(call.Diversion);

        channel.AttachSession(new StubIdentityCallSession(
            remoteAssertedIdentity: "sip:trunk-caller@carrier.example",
            diversion: "sip:diverted-from@pbx.example"));

        Assert.Equal("sip:trunk-caller@carrier.example", call.RemoteAssertedIdentity);
        Assert.Equal("sip:diverted-from@pbx.example", call.Diversion);
    }

    // Minimal inbound-ringing session fake exposing only identity/diversion; everything else no-op.
    private sealed class StubIdentityCallSession(string? remoteAssertedIdentity, string? diversion)
        : ISipCallSession
    {
        public string CallId => "identity-stub-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:remote@127.0.0.1";
        public SipDialogState State => SipDialogState.Ringing;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => true;
        public string? RemoteAssertedIdentity => remoteAssertedIdentity;
        public string? Diversion => diversion;
        public string? RemoteSdp => null;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);
        public IPEndPoint? RemoteSignalingEndPoint => new(IPAddress.Loopback, 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }
        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived { add { } remove { } }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null)
            => Task.CompletedTask;

        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendDtmfAsync(char digit, int durationMs = 160, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendInfoAsync(string contentType, string body, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendReferAsync(string referTo, string? referredBy = null, bool suppressSubscription = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendOptionsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
