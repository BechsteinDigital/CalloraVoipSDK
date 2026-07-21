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

    /// <summary>
    /// Wie die <c>long</c>-Variante, aber für <see cref="double"/>-Metriken (z. B. Jitter). Nutzt einen
    /// relativen Floor (<paramref name="toleranceRatio"/> vom Startsockel) mit absolutem Mindest-Floor,
    /// damit ein Startsockel nahe 0 keine Fehlalarme erzeugt.
    /// Erwartet nicht-negative, finite Werte (z. B. Jitter/RTT in ms); negative Startsockel brechen die
    /// "Aufwärts"-Semantik.
    /// </summary>
    /// <param name="samples">Chronologische Messreihe (mindestens 2 Werte).</param>
    /// <param name="selector">Extrahiert die zu prüfende Metrik.</param>
    /// <param name="toleranceRatio">Erlaubtes relatives Wachstum (z. B. 0.20 = 20 %).</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    public static TrendResult NoUpwardDrift<T>(
        IReadOnlyList<T> samples,
        Func<T, double> selector,
        double toleranceRatio = 0.10,
        string metricName = "value")
    {
        if (samples.Count < 2)
            return new TrendResult(false, $"{metricName}: zu wenige Samples ({samples.Count}).");

        var bucket = Math.Max(1, samples.Count / 5);
        var start = MedianOfDouble(samples.Take(bucket).Select(selector));
        var end = MedianOfDouble(samples.Skip(samples.Count - bucket).Select(selector));

        if (!double.IsFinite(start) || !double.IsFinite(end))
            throw new ArgumentException(
                $"{metricName}: nicht-finite Metrikwerte (NaN/Infinity) werden nicht unterstützt.",
                nameof(selector));

        var tolerance = Math.Max(1e-6, Math.Abs(start) * toleranceRatio);
        var threshold = start + tolerance;
        var hasDrift = end > threshold;
        var detail =
            $"{metricName}: Start≈{start:F3}, Ende≈{end:F3}, Schwelle={threshold:F3} " +
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

    private static double MedianOfDouble(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0d;
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[mid] : (ordered[mid - 1] + ordered[mid]) / 2d;
    }
}
