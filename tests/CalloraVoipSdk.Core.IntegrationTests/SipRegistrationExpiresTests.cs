using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Guards the registration-refresh lifetime selection against REGISTER churn: a registrar
/// echoes all bindings of the address-of-record, each with its own <c>expires</c> as the
/// remaining (counting-down) lifetime. The refresh timer must latch onto our own binding
/// (or the top-level Expires header), never an arbitrary stale binding.
/// </summary>
public sealed class SipRegistrationExpiresTests
{
    private const string OwnContact = "sip:3089553t3@83.135.5.138:14403";

    private static SipResponse ResponseWith(string? contact, string? expiresHeader = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 217.10.68.150:5060;branch=z9hG4bK-test;received=83.135.5.138;rport=14403",
            ["CSeq"] = "4 REGISTER"
        };
        if (contact is not null)
            headers["Contact"] = contact;
        if (expiresHeader is not null)
            headers["Expires"] = expiresHeader;

        return new SipResponse(200, "OK", headers, string.Empty);
    }

    private static int SelectExpires(SipResponse response, string ownContact = OwnContact, int fallback = 600)
    {
        var bindings = SipRegistrationService.ParseRegisteredBindings(response);
        return SipRegistrationService.TryGetEffectiveExpires(response, bindings, ownContact, fallback);
    }

    [Fact]
    public void OwnBinding_selected_evenWhenTopLevelExpiresPresent()
    {
        // RFC 3261 §10.2.1.1: our Contact binding's expires parameter (30) takes precedence over
        // the top-level Expires header (600) — even though the Expires header is now surfaced on
        // responses (no longer stripped as request-only).
        var response = ResponseWith(
            $"<{OwnContact}>;expires=30, <sip:3089553t3@1.2.3.4:5060>;expires=6",
            expiresHeader: "600");

        Assert.Equal(30, SelectExpires(response));
    }

    [Fact]
    public void TopLevelExpires_usedWhenNoOwnBindingMatches()
    {
        // The registrar granted the lifetime via the top-level Expires header (RFC 3261 §10.3)
        // rather than a per-Contact expires for our binding — that value must now be honoured.
        var response = ResponseWith(contact: null, expiresHeader: "1800");

        Assert.Equal(1800, SelectExpires(response));
    }

    [Fact]
    public void OwnContactExpires_beatsTopLevelAndOtherBindings_perRfc10211()
    {
        // Non-own binding first (900), our binding (45), plus a top-level Expires (600): our
        // Contact expires wins over both.
        var response = ResponseWith(
            $"<sip:3089553t3@1.2.3.4:5060>;expires=900, <{OwnContact}>;expires=45",
            expiresHeader: "600");

        Assert.Equal(45, SelectExpires(response));
    }

    [Fact]
    public void OwnBinding_isSelected_ignoringStaleFirstBinding()
    {
        // Stale binding first (remaining 6s), our fresh binding second (57s): the naive
        // first-match parser would return 6 and collapse the refresh interval.
        var response = ResponseWith(
            $"<sip:3089553t3@203.0.113.9:41999>;expires=6, <{OwnContact}>;expires=57");

        Assert.Equal(57, SelectExpires(response));
    }

    [Fact]
    public void OwnBinding_isSelected_acrossMultipleContactRows()
    {
        // Multiple Contact header rows (as many registrars emit them) rather than one
        // comma-joined row — still resolves to our binding's lifetime.
        var response = ResponseWith(
            $"<sip:3089553t3@203.0.113.9:41999>;expires=8\n<{OwnContact}>;expires=120");

        Assert.Equal(120, SelectExpires(response));
    }

    [Fact]
    public void NoUriMatch_fallsBackToLongestRemainingBinding()
    {
        var response = ResponseWith(
            "<sip:3089553t3@203.0.113.9:41999>;expires=11, <sip:3089553t3@198.51.100.7:5060>;expires=290");

        Assert.Equal(290, SelectExpires(response));
    }

    [Fact]
    public void NoBindingsAndNoHeader_usesRequestedFallback()
    {
        var response = ResponseWith(contact: null);

        Assert.Equal(600, SelectExpires(response, fallback: 600));
    }

    [Fact]
    public void RefreshDelay_healthyGrant_refreshesAtEightyPercent()
    {
        Assert.Equal(TimeSpan.FromSeconds(480), SipLineChannel.ComputeRefreshDelay(600, ReregisterOptions.Default));
    }

    [Fact]
    public void RefreshDelay_shortButLegitimateBinding_neverOutlivesTheBinding()
    {
        // A genuinely short grant must still refresh before it lapses (below the churn floor).
        var delay = SipLineChannel.ComputeRefreshDelay(6, ReregisterOptions.Default);

        Assert.True(delay < TimeSpan.FromSeconds(6), $"expected <6s, got {delay}");
        Assert.True(delay >= TimeSpan.FromSeconds(1), $"expected >=1s, got {delay}");
    }

    [Fact]
    public void RefreshDelay_missingExpires_usesDefaultBaseline()
    {
        Assert.Equal(TimeSpan.FromSeconds(240), SipLineChannel.ComputeRefreshDelay(0, ReregisterOptions.Default));
    }

    [Fact]
    public void RefreshDelay_honorsConfiguredRatioAndFloor()
    {
        var options = new ReregisterOptions
        {
            RefreshRatio = 0.5,
            MinRefreshInterval = TimeSpan.FromSeconds(30)
        };

        // 0.5 × 600 = 300 (ratio applied).
        Assert.Equal(TimeSpan.FromSeconds(300), SipLineChannel.ComputeRefreshDelay(600, options));
        // 0.5 × 40 = 20, but the 30s floor lifts it (still below the 39s upper bound).
        Assert.Equal(TimeSpan.FromSeconds(30), SipLineChannel.ComputeRefreshDelay(40, options));
    }
}
