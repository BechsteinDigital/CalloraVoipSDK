namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>
/// Trend-Auswertungen über Soak-Messreihen. Vergleicht einen robusten Anfangs- gegen einen
/// Endsockel (Median der ersten/letzten Fünftel), um einmalige Ausreißer zu ignorieren und
/// echte monotone Drift (Leak-Signatur) zu erkennen.
/// </summary>
public static class TrendAssertions
{
    /// <summary>
    /// Prüft, ob die per <paramref name="selector"/> ausgewählte Metrik über die Reihe
    /// stärker als <paramref name="toleranceRatio"/> (relativ zum Startsockel) aufwärts driftet.
    /// </summary>
    /// <param name="samples">Chronologische Messreihe (mindestens 2 Werte).</param>
    /// <param name="selector">Extrahiert die zu prüfende Metrik aus einem Sample.</param>
    /// <param name="toleranceRatio">Erlaubtes relatives Wachstum (z. B. 0.10 = 10 %).</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    public static TrendResult NoUpwardDrift(
        IReadOnlyList<ResourceSample> samples,
        Func<ResourceSample, long> selector,
        double toleranceRatio = 0.10,
        string metricName = "ManagedBytes")
    {
        if (samples.Count < 2)
            return new TrendResult(false, $"{metricName}: zu wenige Samples ({samples.Count}).");

        var bucket = Math.Max(1, samples.Count / 5);
        var start = Median(samples.Take(bucket).Select(selector));
        var end = Median(samples.Skip(samples.Count - bucket).Select(selector));

        var tolerance = Math.Max(1L, (long)Math.Ceiling(Math.Abs(start) * toleranceRatio));
        var threshold = start + tolerance;
        var hasDrift = end > threshold;
        var detail =
            $"{metricName}: Start≈{start}, Ende≈{end}, Schwelle={threshold} " +
            $"(+{toleranceRatio:P0}) → {(hasDrift ? "DRIFT" : "stabil")}.";
        return new TrendResult(hasDrift, detail);
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0;
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[mid]
            : (ordered[mid - 1] + ordered[mid]) / 2;
    }
}
