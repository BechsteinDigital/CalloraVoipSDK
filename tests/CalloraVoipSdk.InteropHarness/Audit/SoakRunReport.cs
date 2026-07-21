using CalloraVoipSdk.InteropHarness.Diagnostics;
using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.InteropHarness.Audit;

/// <summary>
/// Strukturierter, serialisierbarer Bericht eines einzelnen Soak-Laufs — als CI-Artefakt für die
/// Post-hoc-Diagnose eines nightly-Fehlschlags. Bündelt Lauf-Metadaten (Run-ID, Commit-SHA, Plattform,
/// Runtime), die Szenario-Parameter (Seeds) und die vollständige Messwertreihe samt Fehlschlägen.
/// </summary>
/// <param name="RunId">Eindeutige ID dieses Laufs.</param>
/// <param name="CommitSha">Git-Commit-SHA (aus <c>GITHUB_SHA</c>/<c>SOAK_COMMIT_SHA</c>, sonst "unknown").</param>
/// <param name="Scenario">Name des Soak-Szenarios (z. B. "RtpMediaLeak").</param>
/// <param name="Parameters">Szenario-/Seed-Parameter (z. B. Iterations, Codec, Security).</param>
/// <param name="OsDescription">Betriebssystem-Beschreibung.</param>
/// <param name="OsArchitecture">Prozessor-Architektur.</param>
/// <param name="RuntimeVersion">.NET-Runtime-Version.</param>
/// <param name="CapturedAtUtc">Erstellzeitpunkt des Berichts.</param>
/// <param name="ResourceSeries">Ressourcen-Messreihe (Leak-/Concurrency-/Signaling-Soak).</param>
/// <param name="QualitySeries">Media-Qualitäts-Messreihe (Media-Drift-Soak).</param>
/// <param name="Failures">Strukturierte Einzel-Fehlschläge (z. B. Concurrency-Round-Trips).</param>
public sealed record SoakRunReport(
    string RunId,
    string CommitSha,
    string Scenario,
    IReadOnlyDictionary<string, string> Parameters,
    string OsDescription,
    string OsArchitecture,
    string RuntimeVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ResourceSample> ResourceSeries,
    IReadOnlyList<MediaQualitySnapshot> QualitySeries,
    IReadOnlyList<SoakFailure> Failures);
