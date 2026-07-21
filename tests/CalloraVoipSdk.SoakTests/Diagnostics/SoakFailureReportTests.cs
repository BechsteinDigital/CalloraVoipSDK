using CalloraVoipSdk.InteropHarness.Diagnostics;

namespace CalloraVoipSdk.SoakTests.Diagnostics;

public sealed class SoakFailureReportTests
{
    [Fact]
    public void Describe_EmptySet_ReportsNoFailures()
    {
        var text = SoakFailureReport.Describe(Array.Empty<SoakFailure>());

        Assert.Equal("Keine fehlgeschlagenen Vorgänge.", text);
    }

    [Fact]
    public void Describe_CapturesAllStructuredFields()
    {
        var failures = new[]
        {
            new SoakFailure(Wave: 3, Index: 7, PortPair: "51824↔51825",
                Elapsed: TimeSpan.FromMilliseconds(1234), ExceptionType: "SocketException", Message: "Connection reset"),
        };

        var text = SoakFailureReport.Describe(failures);

        Assert.Contains("1 fehlgeschlagene Vorgänge", text, StringComparison.Ordinal);
        Assert.Contains("Welle 3", text, StringComparison.Ordinal);
        Assert.Contains("#7", text, StringComparison.Ordinal);
        Assert.Contains("51824↔51825", text, StringComparison.Ordinal);
        Assert.Contains("1234 ms", text, StringComparison.Ordinal);
        Assert.Contains("SocketException", text, StringComparison.Ordinal);
        Assert.Contains("Connection reset", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_OrdersByWaveThenIndex()
    {
        var failures = new[]
        {
            new SoakFailure(2, 1, "a", TimeSpan.Zero, "X", "m"),
            new SoakFailure(1, 5, "b", TimeSpan.Zero, "X", "m"),
            new SoakFailure(1, 0, "c", TimeSpan.Zero, "X", "m"),
        };

        var text = SoakFailureReport.Describe(failures);
        var firstPorts = text.IndexOf("Ports c", StringComparison.Ordinal);
        var secondPorts = text.IndexOf("Ports b", StringComparison.Ordinal);
        var thirdPorts = text.IndexOf("Ports a", StringComparison.Ordinal);

        Assert.True(firstPorts < secondPorts && secondPorts < thirdPorts,
            "Reihenfolge muss nach Welle, dann Index sortiert sein.");
    }
}
