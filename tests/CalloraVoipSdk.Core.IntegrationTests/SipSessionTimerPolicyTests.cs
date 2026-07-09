using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 4028 session-timer negotiation (Session-Expires / Min-SE / refresher). Governs how
/// long a dialog is considered alive and who refreshes it; a misparse drops calls at the
/// interval or refreshes needlessly. This surface was untested.
/// </summary>
public sealed class SipSessionTimerPolicyTests
{
    private static SipRequest InviteWith(string? sessionExpires)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sessionExpires is not null)
            headers["Session-Expires"] = sessionExpires;
        return new SipRequest("INVITE", "sip:bob@example.test", headers, string.Empty);
    }

    // ── Inbound validation ──────────────────────────────────────────────────────

    [Fact]
    public void Inbound_without_session_expires_defaults_to_uas_refresher()
    {
        var ok = SipSessionTimerPolicy.TryValidateInboundRequest(
            InviteWith(null), out var code, out _, out var normalized);

        Assert.True(ok);
        Assert.Equal(0, code);
        Assert.Equal($"{SipSessionTimerPolicy.DefaultSessionExpiresSeconds};refresher=uas", normalized);
    }

    [Fact]
    public void Inbound_valid_interval_is_accepted_and_normalized_to_uas()
    {
        var ok = SipSessionTimerPolicy.TryValidateInboundRequest(
            InviteWith("600;refresher=uac"), out var code, out _, out var normalized);

        Assert.True(ok);
        Assert.Equal(0, code);
        Assert.Equal("600;refresher=uas", normalized);
    }

    [Fact]
    public void Inbound_interval_below_min_se_is_rejected_422()
    {
        var ok = SipSessionTimerPolicy.TryValidateInboundRequest(
            InviteWith((SipSessionTimerPolicy.MinSessionExpiresSeconds - 1).ToString()),
            out var code, out var reason, out _);

        Assert.False(ok);
        Assert.Equal(422, code);
        Assert.Equal("Session Interval Too Small", reason);
    }

    [Fact]
    public void Inbound_unparseable_interval_is_rejected_400()
    {
        var ok = SipSessionTimerPolicy.TryValidateInboundRequest(
            InviteWith("notanumber"), out var code, out var reason, out _);

        Assert.False(ok);
        Assert.Equal(400, code);
        Assert.Equal("Bad Request", reason);
    }

    // ── Refresher-role resolution ───────────────────────────────────────────────

    [Theory]
    // refresher=uac → the requester refreshes; refresher=uas → the responder refreshes.
    [InlineData("1800;refresher=uac", true, true)]
    [InlineData("1800;refresher=uac", false, false)]
    [InlineData("1800;refresher=uas", true, false)]
    [InlineData("1800;refresher=uas", false, true)]
    // No refresher parameter → default to the requester.
    [InlineData("1800", true, true)]
    [InlineData("1800", false, false)]
    public void Refresher_role_follows_rfc4028(string header, bool localIsRequester, bool expectedLocalIsRefresher)
    {
        var ok = SipSessionTimerPolicy.TryResolveNegotiation(
            header, localIsRequester, out var interval, out var localIsRefresher);

        Assert.True(ok);
        Assert.Equal(1800, interval);
        Assert.Equal(expectedLocalIsRefresher, localIsRefresher);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0;refresher=uac")]  // zero interval is not usable
    public void Unusable_session_expires_yields_no_negotiation(string? header)
    {
        Assert.False(SipSessionTimerPolicy.TryResolveNegotiation(header, localIsRequester: true, out _, out _));
    }

    // ── Outbound offer ──────────────────────────────────────────────────────────

    [Fact]
    public void Outbound_offer_advertises_timer_support_and_a_uac_refresher()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        SipSessionTimerPolicy.ApplyOutboundOfferHeaders(headers);

        Assert.Equal($"{SipSessionTimerPolicy.DefaultSessionExpiresSeconds};refresher=uac", headers["Session-Expires"]);
        Assert.Equal(SipSessionTimerPolicy.MinSessionExpiresSeconds.ToString(), headers["Min-SE"]);
        Assert.Contains("timer", headers["Supported"]);
    }
}
