using System.Runtime.InteropServices;
using System.Text.Json;
using CalloraVoipSdk.InteropHarness.Diagnostics;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.InteropHarness.Audit;

/// <summary>
/// Schreibt strukturierte <see cref="SoakRunReport"/>-Artefakte (JSON + Markdown-Zeile) für den
/// nightly-Soak. <em>Env-gated</em>: nur wenn <see cref="ArtifactDirEnv"/> gesetzt ist, wird auf Platte
/// geschrieben — lokale Testläufe bleiben so seiteneffektfrei. Der Aufruf gehört VOR die Assertions,
/// damit auch ein fehlschlagender Lauf sein Artefakt (mit Messreihe + Fehlern) hinterlässt.
/// </summary>
public static class SoakArtifactSink
{
    /// <summary>Env-Variable mit dem Zielverzeichnis für Artefakte (ungesetzt ⇒ no-op).</summary>
    public const string ArtifactDirEnv = "SOAK_ARTIFACT_DIR";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Baut einen Bericht mit automatisch erfassten Lauf-Metadaten (Run-ID, SHA, Plattform, Runtime).</summary>
    public static SoakRunReport CreateReport(
        string scenario,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<ResourceSample>? resourceSeries = null,
        IReadOnlyList<MediaQualitySnapshot>? qualitySeries = null,
        IReadOnlyList<SoakFailure>? failures = null) =>
        new(
            RunId: Guid.NewGuid().ToString("n"),
            CommitSha: Environment.GetEnvironmentVariable("GITHUB_SHA")
                       ?? Environment.GetEnvironmentVariable("SOAK_COMMIT_SHA")
                       ?? "unknown",
            Scenario: scenario,
            Parameters: parameters,
            OsDescription: RuntimeInformation.OSDescription,
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            RuntimeVersion: RuntimeInformation.FrameworkDescription,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            ResourceSeries: resourceSeries ?? Array.Empty<ResourceSample>(),
            QualitySeries: qualitySeries ?? Array.Empty<MediaQualitySnapshot>(),
            Failures: failures ?? Array.Empty<SoakFailure>());

    /// <summary>
    /// Schreibt den Bericht als JSON (und eine Markdown-Summenzeile) in <see cref="ArtifactDirEnv"/>,
    /// falls gesetzt. Gibt den JSON-Pfad zurück oder <see langword="null"/> (Env ungesetzt / IO-Fehler).
    /// Best-effort: IO-Fehler dürfen einen Soak nicht zum Absturz bringen.
    /// </summary>
    public static string? TryWrite(SoakRunReport report)
    {
        var dir = Environment.GetEnvironmentVariable(ArtifactDirEnv);
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        try
        {
            Directory.CreateDirectory(dir);
            var stem = $"{Sanitize(report.Scenario)}-{report.RunId}";
            var jsonPath = Path.Combine(dir, stem + ".json");
            File.WriteAllText(jsonPath, ToJson(report));
            File.AppendAllText(Path.Combine(dir, "summary.md"), ToMarkdownRow(report) + "\n");
            return jsonPath;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Serialisiert den Bericht deterministisch als eingerücktes JSON.</summary>
    public static string ToJson(SoakRunReport report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>Eine Markdown-Tabellenzeile: Szenario, SHA, Plattform, Reihenlänge, Fehlerzahl.</summary>
    public static string ToMarkdownRow(SoakRunReport report)
    {
        var seriesLength = report.ResourceSeries.Count + report.QualitySeries.Count;
        var sha = report.CommitSha.Length > 8 ? report.CommitSha[..8] : report.CommitSha;
        return $"| {report.Scenario} | {report.RunId[..8]} | {sha} | {report.OsArchitecture} | " +
               $"{seriesLength} Punkte | {report.Failures.Count} Fehler |";
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
