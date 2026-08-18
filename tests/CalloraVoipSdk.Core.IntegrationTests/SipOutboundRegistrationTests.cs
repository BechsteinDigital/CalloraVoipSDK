using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-067b (RFC 5626 §4.1): an outbound REGISTER — one carrying a UA instance id — puts the <c>ob</c> parameter
/// in the Contact URI (so the edge proxy reuses this registered flow) alongside <c>+sip.instance</c> and
/// <c>reg-id</c>. A plain registration (no instance id) carries none of these.
/// </summary>
public sealed class SipOutboundRegistrationTests
{
    private const string ContactUri = "sip:bob@192.0.2.1:5060";

    [Fact]
    public void An_outbound_registration_contact_carries_ob_instance_and_reg_id()
    {
        // Bound to the request property the production path reads, so this stays a test of what actually
        // feeds the builder rather than of a literal that happens to match today.
        var request = new SipRegistrationRequest
        {
            Username = "bob", Password = "secret", Domain = "example.com", InstanceId = "urn:uuid:1b4c-2",
        };
        var contact = SipRegistrationService.BuildContactHeaderValue(ContactUri, 600, request.InstanceId);

        Assert.Contains($"<{ContactUri};ob>", contact); // RFC 5626 §4.1: ob is a URI parameter (inside <>)
        // RFC 5626 §4.1: the parameter NAME is the bare token +sip.instance; only the value is quoted.
        Assert.Contains(";+sip.instance=\"<urn:uuid:1b4c-2>\"", contact);
        Assert.DoesNotContain("\"+sip.instance\"", contact); // the parameter name must NOT be quoted (was malformed)
        Assert.Contains(";reg-id=1", contact);
        Assert.Contains(";expires=600", contact);
    }

    [Fact]
    public void Ob_appends_after_an_existing_transport_uri_parameter()
    {
        // Production Contact URIs always carry a ;transport= parameter; ob must append as the last URI parameter.
        var contact = SipRegistrationService.BuildContactHeaderValue(
            "sip:bob@192.0.2.1:5060;transport=tcp", 600, "urn:uuid:1b4c-2");

        Assert.Contains("<sip:bob@192.0.2.1:5060;transport=tcp;ob>", contact);
    }

    [Fact]
    public void A_plain_registration_contact_has_no_outbound_parameters()
    {
        var contact = SipRegistrationService.BuildContactHeaderValue(ContactUri, 600, instanceId: null);

        Assert.Equal($"<{ContactUri}>;expires=600", contact);
        Assert.DoesNotContain(";ob", contact);
        Assert.DoesNotContain("sip.instance", contact);
        Assert.DoesNotContain("reg-id", contact);
    }
}
