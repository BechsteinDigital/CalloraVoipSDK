using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Reading the retargeting history of an inbound INVITE out of whichever header the carrier chose.
/// Diversion (RFC 5806) and History-Info (RFC 4244) answer the same question, are ordered opposite
/// ways, and no carrier sends both consistently — a consumer reading only one is silently blind with
/// the other half of the market, which makes a forwarded call look exactly like a direct one.
/// </summary>
public sealed class SipDiversionHistoryTests
{
    private const string Us = "sip:pbx@example.com";

    [Fact]
    public void A_single_diversion_names_the_party_that_forwarded()
    {
        var chain = SipDiversionHistory.Parse(
            historyInfoRows: null,
            diversionRows: ["<sip:mobile@example.com>;reason=unconditional"],
            currentTargetUri: Us);

        Assert.Equal(["sip:mobile@example.com"], chain);
    }

    [Fact]
    public void Diversion_rows_arrive_newest_first_and_come_back_oldest_first()
    {
        // RFC 5806 puts the most recent diverting party at the top. Handing that order through
        // unchanged would report the last hop as the number the caller originally dialled.
        var chain = SipDiversionHistory.Parse(
            historyInfoRows: null,
            diversionRows:
            [
                "<sip:second@example.com>;reason=no-answer",
                "<sip:first@example.com>;reason=unconditional",
            ],
            currentTargetUri: Us);

        Assert.Equal(["sip:first@example.com", "sip:second@example.com"], chain);
    }

    [Fact]
    public void Diversion_entries_may_share_one_row_as_a_comma_list()
    {
        var chain = SipDiversionHistory.Parse(
            historyInfoRows: null,
            diversionRows: ["<sip:second@example.com>;reason=no-answer, <sip:first@example.com>"],
            currentTargetUri: Us);

        Assert.Equal(["sip:first@example.com", "sip:second@example.com"], chain);
    }

    [Fact]
    public void History_info_is_ordered_by_its_index_not_by_arrival()
    {
        var chain = SipDiversionHistory.Parse(
            historyInfoRows:
            [
                "<sip:second@example.com>;index=1.1",
                "<sip:first@example.com>;index=1",
            ],
            diversionRows: null,
            currentTargetUri: Us);

        Assert.Equal(["sip:first@example.com", "sip:second@example.com"], chain);
    }

    [Fact]
    public void History_info_indexes_sort_as_numbers_not_as_text()
    {
        // "1.10" sorts before "1.2" as a string. On a call forwarded more than nine times along one
        // branch that silently reverses the two most recent hops.
        var chain = SipDiversionHistory.Parse(
            historyInfoRows:
            [
                "<sip:tenth@example.com>;index=1.10",
                "<sip:second@example.com>;index=1.2",
            ],
            diversionRows: null,
            currentTargetUri: Us);

        Assert.Equal(["sip:second@example.com", "sip:tenth@example.com"], chain);
    }

    [Fact]
    public void History_info_does_not_report_us_as_a_party_that_forwarded()
    {
        // Its entries are targets, not forwarders, and the last one is where the request is now.
        var chain = SipDiversionHistory.Parse(
            historyInfoRows:
            [
                "<sip:mobile@example.com>;index=1",
                "<sip:pbx@example.com>;index=1.1",
            ],
            diversionRows: null,
            currentTargetUri: Us);

        Assert.Equal(["sip:mobile@example.com"], chain);
    }

    [Fact]
    public void Our_own_entry_is_recognised_by_uri_comparison_not_by_string_equality()
    {
        // RFC 3261 §19.1.4: case in the host part does not distinguish two addresses.
        var chain = SipDiversionHistory.Parse(
            historyInfoRows:
            [
                "<sip:mobile@example.com>;index=1",
                "<sip:pbx@EXAMPLE.COM>;index=1.1",
            ],
            diversionRows: null,
            currentTargetUri: Us);

        Assert.Equal(["sip:mobile@example.com"], chain);
    }

    [Fact]
    public void History_info_wins_when_a_carrier_sends_both()
    {
        // It states its order explicitly where Diversion relies on a convention, so when the two
        // disagree the one that says what it means is the one to believe.
        var chain = SipDiversionHistory.Parse(
            historyInfoRows:
            [
                "<sip:from-history@example.com>;index=1",
                "<sip:pbx@example.com>;index=1.1",
            ],
            diversionRows: ["<sip:from-diversion@example.com>"],
            currentTargetUri: Us);

        Assert.Equal(["sip:from-history@example.com"], chain);
    }

    [Fact]
    public void A_history_info_entry_carrying_a_reason_still_yields_its_uri()
    {
        var chain = SipDiversionHistory.Parse(
            historyInfoRows: ["<sip:mobile@example.com?Reason=SIP%3Bcause%3D302>;index=1"],
            diversionRows: null,
            currentTargetUri: Us);

        Assert.Single(chain);
        Assert.StartsWith("sip:mobile@example.com", chain[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_reported_is_an_empty_chain_not_a_claim_that_nothing_happened()
    {
        // An empty result means no retargeting was reported. It does not mean the call was not
        // forwarded — a carrier that sends neither header leaves us unable to tell the difference.
        var chain = SipDiversionHistory.Parse(
            historyInfoRows: null,
            diversionRows: null,
            currentTargetUri: Us);

        Assert.Empty(chain);
    }

    [Fact]
    public void An_unparsable_row_is_skipped_rather_than_failing_the_call()
    {
        var chain = SipDiversionHistory.Parse(
            historyInfoRows: null,
            diversionRows: ["", "   ", "<sip:mobile@example.com>"],
            currentTargetUri: Us);

        Assert.Equal(["sip:mobile@example.com"], chain);
    }
}
