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
        var contact = SipRegistrationService.BuildContactHeaderValue(ContactUri, 600, "urn:uuid:1b4c-2");

        Assert.Contains($"<{ContactUri};ob>", contact); // RFC 5626 §4.1: ob is a URI parameter (inside <>)
        Assert.Contains(";\"+sip.instance\"=\"<urn:uuid:1b4c-2>\"", contact);
        Assert.Contains(";reg-id=1", contact);
        Assert.Contains(";expires=600", contact);
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
