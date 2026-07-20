using CalloraVoipSdk.Core.Application.Media.Rtcp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The opaque RTCP CNAME generator (CF-006, RFC 7022): a random per-session identifier that never leaks the
/// machine name and is distinct on every call, so separate sessions from one installation are not correlatable.
/// </summary>
public sealed class RtcpCnameTests
{
    [Fact]
    public void Each_cname_is_non_empty_and_distinct()
    {
        var a = RtcpCname.NewOpaque();
        var b = RtcpCname.NewOpaque();
        Assert.False(string.IsNullOrWhiteSpace(a));
        Assert.NotEqual(a, b); // 96 random bits — a collision here would be astronomically unlikely
    }

    [Fact]
    public void A_cname_never_contains_the_machine_name()
    {
        var machine = Environment.MachineName;
        for (var i = 0; i < 50; i++)
        {
            var cname = RtcpCname.NewOpaque();
            Assert.DoesNotContain(machine, cname, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_cname_is_sdes_safe_base64url()
    {
        // No '+', '/', or '=' — the value is length-prefixed on the wire but staying in the base64url alphabet
        // keeps it printable and free of SDES-item delimiters.
        var cname = RtcpCname.NewOpaque();
        Assert.DoesNotContain('+', cname);
        Assert.DoesNotContain('/', cname);
        Assert.DoesNotContain('=', cname);
        Assert.All(cname, c => Assert.True(char.IsLetterOrDigit(c) || c is '-' or '_', $"unexpected char '{c}'"));
    }
}
