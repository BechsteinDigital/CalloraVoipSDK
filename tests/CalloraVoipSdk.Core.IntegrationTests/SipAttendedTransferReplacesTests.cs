using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 5589 attended transfer: the outgoing REFER carries an RFC 3891 <c>Replaces</c> embedded in
/// the <c>Refer-To</c> URI, identifying the consultation dialog from the transfer target's
/// perspective. Covers the header formatting, URI-escaping, and the tag direction that decides
/// whether the far end can match the dialog.
/// </summary>
public class SipAttendedTransferReplacesTests
{
    [Fact]
    public void ToHeaderValue_roundtrips_through_TryParse()
    {
        var replaces = new SipReplacesHeaderValue("call-abc", toTag: "totag1", fromTag: "fromtag2", earlyOnly: false);

        var header = replaces.ToHeaderValue();

        Assert.Equal("call-abc;to-tag=totag1;from-tag=fromtag2", header);
        Assert.True(SipReplacesHeaderValue.TryParse(header, out var parsed));
        Assert.Equal("call-abc", parsed!.CallId);
        Assert.Equal("totag1", parsed.ToTag);
        Assert.Equal("fromtag2", parsed.FromTag);
        Assert.False(parsed.EarlyOnly);
    }

    [Fact]
    public void ToHeaderValue_includes_early_only_when_set()
    {
        var replaces = new SipReplacesHeaderValue("c", "t", "f", earlyOnly: true);

        Assert.Equal("c;to-tag=t;from-tag=f;early-only", replaces.ToHeaderValue());
    }

    [Fact]
    public void BuildReferToUri_wraps_in_anglebrackets_and_escapes_replaces()
    {
        var replaces = new SipReplacesHeaderValue("call-abc", "totag1", "fromtag2", earlyOnly: false);

        var referTo = replaces.BuildReferToUri("sip:bob@example.com");

        Assert.StartsWith("<sip:bob@example.com?Replaces=", referTo);
        Assert.EndsWith(">", referTo);

        // The raw ';' / '=' of the Replaces value must be percent-escaped inside the URI header,
        // otherwise they would be parsed as URI parameters rather than the Replaces value.
        var replacesParam = referTo.Split("?Replaces=", 2)[1].TrimEnd('>');
        Assert.DoesNotContain(';', replacesParam);
        Assert.DoesNotContain('=', replacesParam[..replacesParam.IndexOf("%3D", StringComparison.Ordinal)]);

        Assert.True(SipReplacesHeaderValue.TryParse(Uri.UnescapeDataString(replacesParam), out var parsed));
        Assert.Equal("call-abc", parsed!.CallId);
        Assert.Equal("totag1", parsed.ToTag);
        Assert.Equal("fromtag2", parsed.FromTag);
    }

    [Fact]
    public void BuildAttendedReferTo_maps_tags_to_target_perspective()
    {
        // Consultation dialog (us <-> target): our local tag = "sdkTag", the target's tag = "bobTag".
        var referTo = AttendedTransferReferTo.Build(
            callId: "consult-call-id",
            localTag: "sdkTag",
            remoteTag: "bobTag",
            remoteUri: "sip:bob@pbx.example");

        Assert.NotNull(referTo);
        var replacesParam = referTo!.Split("?Replaces=", 2)[1].TrimEnd('>');
        Assert.True(SipReplacesHeaderValue.TryParse(Uri.UnescapeDataString(replacesParam), out var parsed));

        Assert.Equal("consult-call-id", parsed!.CallId);
        // RFC 3891: the target matches to-tag against its own local tag and from-tag against its
        // remote tag — so to-tag = the target's tag (our remote tag), from-tag = our tag.
        Assert.Equal("bobTag", parsed.ToTag);
        Assert.Equal("sdkTag", parsed.FromTag);
    }

    [Fact]
    public void BuildAttendedReferTo_returns_null_when_dialog_not_established()
    {
        // No remote tag yet (consultation not answered) -> caller falls back to a plain REFER.
        Assert.Null(AttendedTransferReferTo.Build("id", "sdkTag", remoteTag: null, remoteUri: "sip:bob@pbx"));
        Assert.Null(AttendedTransferReferTo.Build("id", localTag: null, remoteTag: "bobTag", remoteUri: "sip:bob@pbx"));
        Assert.Null(AttendedTransferReferTo.Build("id", "a", "b", remoteUri: "not-a-sip-uri"));
    }

    [Fact]
    public void BuildAttendedReferTo_normalizes_bracketed_target_to_addr_spec()
    {
        var referTo = AttendedTransferReferTo.Build(
            callId: "id",
            localTag: "a",
            remoteTag: "b",
            remoteUri: "<sip:bob@pbx.example;transport=udp>");

        Assert.NotNull(referTo);
        Assert.StartsWith("<sip:bob@pbx.example?Replaces=", referTo);
    }

    [Fact]
    public async Task AttendedTransferAsync_emits_REFER_with_replaces_of_the_consultation_dialog()
    {
        // Two established legs with deliberately different tags/Call-IDs so the test proves the
        // channel reads the CONSULTATION (target) leg — not the transferee leg — for the Replaces.
        var transfereeLeg = new FakeCallSession
        {
            CallId = "call-A",
            RemoteUri = "sip:alice@pbx",
            LocalTag = "aLocal",
            RemoteTag = "aRemote"
        };
        var consultationLeg = new FakeCallSession
        {
            CallId = "consult-B",
            RemoteUri = "sip:bob@pbx.example",
            LocalTag = "sdkTag",
            RemoteTag = "bobTag"
        };

        var transfereeChannel = CreateChannel();
        transfereeChannel.AttachSession(transfereeLeg);
        var consultationChannel = CreateChannel();
        consultationChannel.AttachSession(consultationLeg);

        var accepted = await transfereeChannel.AttendedTransferAsync(
            consultationChannel, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(accepted);
        // The REFER is sent on the transferee's dialog; the consultation leg gets no REFER.
        Assert.NotNull(transfereeLeg.CapturedReferTo);
        Assert.Null(consultationLeg.CapturedReferTo);
        // Refer-To targets the consultation remote party, with a Replaces of the consultation dialog.
        Assert.StartsWith("<sip:bob@pbx.example?Replaces=", transfereeLeg.CapturedReferTo);

        var replacesParam = transfereeLeg.CapturedReferTo!.Split("?Replaces=", 2)[1].TrimEnd('>');
        Assert.True(SipReplacesHeaderValue.TryParse(Uri.UnescapeDataString(replacesParam), out var parsed));
        Assert.Equal("consult-B", parsed!.CallId);       // the consultation dialog, not "call-A"
        Assert.Equal("bobTag", parsed.ToTag);             // the target's tag
        Assert.Equal("sdkTag", parsed.FromTag);           // our tag on the consultation leg
    }

    private static SipCoreCallChannel CreateChannel() => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        SrtpPolicy.Optional,
        policySource: "test");

    /// <summary>Minimal established <see cref="ISipCallSession"/> that captures the REFER it is asked to send.</summary>
    private sealed class FakeCallSession : ISipCallSession
    {
        public string? CapturedReferTo { get; private set; }

        public required string CallId { get; init; }
        public required string RemoteUri { get; init; }
        public string? LocalTag { get; init; }
        public string? RemoteTag { get; init; }

        public string LocalUri => "sip:sdk@127.0.0.1";
        public SipDialogState State => SipDialogState.Established;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => false;
        public string? RemoteAssertedIdentity => null;
        public string? RemoteSdp => null;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }
        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived { add { } remove { } }

        public Task<bool> SendReferAsync(string referTo, string? referredBy = null, bool suppressSubscription = false, CancellationToken ct = default)
        {
            CapturedReferTo = referTo;
            return Task.FromResult(true);
        }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default) => Task.CompletedTask;
        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, int durationMs = 160, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SendOptionsAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default) => Task.FromResult(true);
        public void Dispose() { }
    }
}
