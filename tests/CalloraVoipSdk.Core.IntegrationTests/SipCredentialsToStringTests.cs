using CalloraVoipSdk.Core.Domain.Lines;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [App/Domain] #165 P1-3: SipCredentials is a record, so the compiler-generated ToString would emit every
/// property — including the password — into any structured log, exception dump or debugger inspection. These
/// tests pin the explicit allow-list ToString that surfaces only the non-secret fields and redacts the password.
/// </summary>
public sealed class SipCredentialsToStringTests
{
    [Fact]
    public void ToString_surfaces_non_secret_fields_and_never_the_password()
    {
        var credentials = new SipCredentials("alice", "S3CR3T", "example.org");

        var text = credentials.ToString();

        Assert.DoesNotContain("S3CR3T", text);
        Assert.Contains("alice", text);
        Assert.Contains("example.org", text);
    }

    [Fact]
    public void Interpolation_and_string_format_also_redact_the_password()
    {
        var credentials = new SipCredentials("bob", "hunter2");

        Assert.DoesNotContain("hunter2", $"{credentials}");
        Assert.DoesNotContain("hunter2", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", credentials));
    }
}
