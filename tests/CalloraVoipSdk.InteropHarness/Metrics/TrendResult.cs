namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Ergebnis einer Trend-Auswertung über eine Soak-Messreihe.</summary>
/// <param name="HasDrift">True, wenn die Reihe die tolerierte Aufwärts-Drift überschreitet.</param>
/// <param name="Detail">Menschlich lesbare Begründung (Metrik, Start-/Endwert, Schwelle).</param>
public readonly record struct TrendResult(bool HasDrift, string Detail);
