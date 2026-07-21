namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Ergebnis einer <see cref="ResourcePlateauAssertions"/>-Prüfung.</summary>
/// <param name="Exceeded">Der Endsockel überschreitet Baseline + Toleranz (Leak-Verdacht).</param>
/// <param name="Skipped">Metrik auf dieser Plattform nicht erfasst (Sentinel &lt; 0) — nicht gewertet.</param>
/// <param name="Detail">Menschenlesbare Begründung für Assert-Meldungen.</param>
public readonly record struct PlateauResult(bool Exceeded, bool Skipped, string Detail);
