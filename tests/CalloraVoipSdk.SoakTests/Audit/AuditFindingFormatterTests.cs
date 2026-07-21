using CalloraVoipSdk.InteropHarness.Audit;

namespace CalloraVoipSdk.SoakTests.Audit;

public sealed class AuditFindingFormatterTests
{
    [Fact]
    public void ToMarkdownRow_RendersAllFields_AsSinglePipeRow()
    {
        var finding = new Finding(
            Fid: "F002",
            Type: "Soak-Leak",
            Evidence: "RtpMediaLeakSoakTests",
            Symptom: "ManagedBytes driftet über 10.000 Round-Trips",
            RootCause: "unklar",
            Location: "src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs:255",
            FixProposal: "n/a — dokumentiert",
            Severity: "offen",
            Status: "offen");

        var row = AuditFindingFormatter.ToMarkdownRow(finding);

        Assert.StartsWith("| F002 | Soak-Leak |", row);
        Assert.EndsWith("| offen |", row);
        Assert.DoesNotContain("\n", row);
    }

    [Fact]
    public void ToMarkdownRow_EscapesPipeCharacters()
    {
        var finding = new Finding(
            Fid: "F003", Type: "Wire-Robustheit", Evidence: "x",
            Symptom: "SDP a|b kaputt", RootCause: "x", Location: "x",
            FixProposal: "x", Severity: "x", Status: "x");

        var row = AuditFindingFormatter.ToMarkdownRow(finding);

        Assert.Contains(@"a\|b", row);
    }
}
