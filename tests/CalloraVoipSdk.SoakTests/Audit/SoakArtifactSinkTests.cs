using System.Text.Json;
using CalloraVoipSdk.InteropHarness.Audit;
using CalloraVoipSdk.InteropHarness.Diagnostics;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Audit;

public sealed class SoakArtifactSinkTests
{
    private static SoakRunReport SampleReport() => new(
        RunId: "abcdef0123456789",
        CommitSha: "0123456789abcdef",
        Scenario: "RtpMediaLeak",
        Parameters: new Dictionary<string, string> { ["Iterations"] = "500" },
        OsDescription: "TestOS",
        OsArchitecture: "X64",
        RuntimeVersion: ".NET Test",
        CapturedAtUtc: DateTimeOffset.UnixEpoch,
        ResourceSeries: new[] { new ResourceSample(0, 1, 2, 3, 4, 5, 6, 7) },
        QualitySeries: Array.Empty<MediaQualitySnapshot>(),
        Failures: new[] { new SoakFailure(1, 2, "a↔b", TimeSpan.FromMilliseconds(5), "X", "m") });

    [Fact]
    public void ToJson_RoundTripsMetadataAndSeries()
    {
        var json = SoakArtifactSink.ToJson(SampleReport());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("RtpMediaLeak", root.GetProperty("Scenario").GetString());
        Assert.Equal("0123456789abcdef", root.GetProperty("CommitSha").GetString());
        Assert.Equal("500", root.GetProperty("Parameters").GetProperty("Iterations").GetString());
        Assert.Equal(1, root.GetProperty("ResourceSeries").GetArrayLength());
        Assert.Equal(1, root.GetProperty("Failures").GetArrayLength());
    }

    [Fact]
    public void ToMarkdownRow_ContainsScenarioShaAndCounts()
    {
        var row = SoakArtifactSink.ToMarkdownRow(SampleReport());

        Assert.Contains("RtpMediaLeak", row, StringComparison.Ordinal);
        Assert.Contains("01234567", row, StringComparison.Ordinal); // SHA gekürzt
        Assert.Contains("1 Fehler", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TryWrite_WithoutEnv_IsNoOp()
    {
        var previous = Environment.GetEnvironmentVariable(SoakArtifactSink.ArtifactDirEnv);
        Environment.SetEnvironmentVariable(SoakArtifactSink.ArtifactDirEnv, null);
        try
        {
            Assert.Null(SoakArtifactSink.TryWrite(SampleReport()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SoakArtifactSink.ArtifactDirEnv, previous);
        }
    }

    [Fact]
    public void TryWrite_WithEnv_WritesJsonAndSummary()
    {
        var previous = Environment.GetEnvironmentVariable(SoakArtifactSink.ArtifactDirEnv);
        var dir = Path.Combine(Path.GetTempPath(), "soak-artifacts-" + Guid.NewGuid().ToString("n"));
        Environment.SetEnvironmentVariable(SoakArtifactSink.ArtifactDirEnv, dir);
        try
        {
            var path = SoakArtifactSink.TryWrite(SampleReport());

            Assert.NotNull(path);
            Assert.True(File.Exists(path!));
            Assert.Contains("RtpMediaLeak", File.ReadAllText(path!), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir, "summary.md")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SoakArtifactSink.ArtifactDirEnv, previous);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateReport_PopulatesRunMetadata()
    {
        var report = SoakArtifactSink.CreateReport("Scn", new Dictionary<string, string>());

        Assert.False(string.IsNullOrWhiteSpace(report.RunId));
        Assert.False(string.IsNullOrWhiteSpace(report.OsDescription));
        Assert.False(string.IsNullOrWhiteSpace(report.RuntimeVersion));
    }
}
