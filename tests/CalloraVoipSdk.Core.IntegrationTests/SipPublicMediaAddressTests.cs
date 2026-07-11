using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins CORE-017: an opt-in public media address (SipAccount.PublicMediaHost, surfaced to the call
/// channel) forces the SDP media connection line; when unset the media address stays auto-resolved.
/// </summary>
public sealed class SipPublicMediaAddressTests
{
    private const string PublicIp = "203.0.113.50";

    private static string PlainOffer(int mediaPort) =>
        "v=0\r\n"
        + "o=- 1 1 IN IP4 198.51.100.7\r\n"
        + "s=peer\r\n"
        + "c=IN IP4 198.51.100.7\r\n"
        + "t=0 0\r\n"
        + $"m=audio {mediaPort} RTP/AVP 0\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + "a=sendrecv\r\n";

    private static SipCoreCallChannel CreateChannel(IPAddress? publicMedia) => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        SrtpPolicy.Disabled,
        policySource: "test",
        iceAgent: null,
        preferredCodecNames: null,
        advertisedPublicMediaAddress: publicMedia);

    private static async Task<string> AnswerSdpAsync(IPAddress? publicMedia)
    {
        using var channel = CreateChannel(publicMedia);
        var session = new FakeInboundSession(PlainOffer(30000));
        channel.AttachSession(session);
        await channel.AnswerAsync(CancellationToken.None);
        Assert.NotNull(session.CapturedAnswerSdp);
        return session.CapturedAnswerSdp!;
    }

    [Fact]
    public async Task Configured_public_media_address_is_forced_into_the_answer_sdp()
    {
        var answerSdp = await AnswerSdpAsync(IPAddress.Parse(PublicIp));

        Assert.Contains($"c=IN IP4 {PublicIp}", answerSdp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_override_the_answer_sdp_does_not_use_the_public_address()
    {
        var answerSdp = await AnswerSdpAsync(publicMedia: null);

        Assert.DoesNotContain(PublicIp, answerSdp, StringComparison.Ordinal);
    }

    // Minimal inbound-ringing session fake that captures the answer SDP the channel emits.
    private sealed class FakeInboundSession(string remoteSdp) : ISipCallSession
    {
        public string? CapturedAnswerSdp { get; private set; }

        public string CallId => "public-media-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:peer@198.51.100.7";
        public SipDialogState State { get; private set; } = SipDialogState.Ringing;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => true;
        public string? RemoteAssertedIdentity => null;
        public string? RemoteSdp => remoteSdp;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);
        public IPEndPoint? RemoteSignalingEndPoint => new(IPAddress.Parse("198.51.100.7"), 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }
        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived { add { } remove { } }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default)
        {
            CapturedAnswerSdp = sessionDescription;
            State = SipDialogState.Established;
            return Task.CompletedTask;
        }

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
