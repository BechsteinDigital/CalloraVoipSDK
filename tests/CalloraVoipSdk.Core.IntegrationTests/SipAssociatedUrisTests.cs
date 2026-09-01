using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Reading the numbers a registrar says belong to this registration (RFC 3455 §5.1). It is the one
/// source that needs neither an operator typing a list nor a call to have happened — and the one every
/// carrier registrar offers while a box on the local network generally does not.
/// </summary>
public sealed class SipAssociatedUrisTests
{
    [Fact]
    public void Announced_addresses_are_read_in_the_order_the_registrar_gave_them()
    {
        // The first entry is the default public identity — the number the network uses when a request
        // does not say otherwise. Sorting would throw away which one is the main line.
        var uris = SipAssociatedUris.Parse(["<sip:+493011@carrier.example>, <sip:+493022@carrier.example>"]);

        Assert.Equal(["sip:+493011@carrier.example", "sip:+493022@carrier.example"], uris);
    }

    [Fact]
    public void Several_header_rows_are_one_list()
    {
        // A registrar may fold them into one row or send them separately; both mean the same thing.
        var uris = SipAssociatedUris.Parse(["<sip:+493011@carrier.example>", "<tel:+493022>"]);

        Assert.Equal(["sip:+493011@carrier.example", "tel:+493022"], uris);
    }

    [Fact]
    public void A_display_name_containing_a_comma_does_not_split_the_entry()
    {
        // Splitting on every comma cuts "Meier, Dana" in half and yields two entries, neither an address.
        var uris = SipAssociatedUris.Parse(["\"Meier, Dana\" <sip:+493011@carrier.example>"]);

        Assert.Equal(["sip:+493011@carrier.example"], uris);
    }

    [Fact]
    public void Header_parameters_are_not_part_of_the_address()
    {
        var uris = SipAssociatedUris.Parse(["<sip:+493011@carrier.example>;q=1.0"]);

        Assert.Equal(["sip:+493011@carrier.example"], uris);
    }

    [Fact]
    public void A_transport_inside_the_brackets_belongs_to_the_uri_and_stays()
    {
        // The distinction RFC 3261 §20 draws: inside the angle brackets a semicolon is part of the
        // URI, outside them it starts the header parameters. Cutting both would hand back an address
        // that lost its transport.
        var uris = SipAssociatedUris.Parse(["<sip:+493011@carrier.example;transport=tcp>;q=1.0"]);

        Assert.Equal(["sip:+493011@carrier.example;transport=tcp"], uris);
    }

    [Fact]
    public void The_same_address_announced_twice_is_one_entry()
    {
        var uris = SipAssociatedUris.Parse(
            ["<sip:+493011@carrier.example>", "<SIP:+493011@carrier.example>"]);

        Assert.Single(uris);
    }

    [Fact]
    public void A_registrar_that_says_nothing_yields_nothing()
    {
        // "Nothing" means nobody said, never that the line has no numbers. Read the other way, a
        // silent registrar becomes a line nobody can reach.
        Assert.Empty(SipAssociatedUris.Parse(null));
        Assert.Empty(SipAssociatedUris.Parse([]));
        Assert.Empty(SipAssociatedUris.Parse(["   "]));
    }

    [Fact]
    public void A_row_that_carries_no_address_is_skipped_rather_than_ending_the_list()
    {
        var uris = SipAssociatedUris.Parse(["<>", "<sip:+493011@carrier.example>"]);

        Assert.Equal(["sip:+493011@carrier.example"], uris);
    }
}
