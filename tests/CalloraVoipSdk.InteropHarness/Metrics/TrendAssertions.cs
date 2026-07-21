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

    /// <summary>
    /// Least-Squares-Steigung der per <paramref name="selector"/> gewählten Metrik über den
    /// Sample-Index (x = 0..n-1). Robuster als ein Start-vs-Ende-Vergleich: einmalige Ausreißer
    /// oder ein einzelner Sockelsprung kippen die Ausgleichsgerade kaum, echte monotone Drift schon.
    /// Die Reihe MUSS bereits warm gelaufen sein (Kaltstart-Ramp vorher verwerfen), sonst misst
    /// die Steigung den Warmlauf statt eines Leaks.
    /// </summary>
    /// <param name="samples">Chronologische, warm gelaufene Messreihe (mindestens 3 Werte).</param>
    /// <param name="selector">Extrahiert die zu prüfende Metrik.</param>
    /// <param name="maxSlopePerSample">Absolute Obergrenze der Steigung in Metrik-Einheiten pro Sample.</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    public static TrendResult NoUpwardSlope(
        IReadOnlyList<ResourceSample> samples,
        Func<ResourceSample, double> selector,
        double maxSlopePerSample,
        string metricName)
    {
        if (samples.Count < 3)
            return new TrendResult(false, $"{metricName}: zu wenige Samples ({samples.Count}) für eine Regression.");

        var ys = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++)
            ys[i] = selector(samples[i]);

        var slope = LeastSquaresSlope(ys);
        var hasDrift = slope > maxSlopePerSample;
        var detail =
            $"{metricName}: Steigung≈{slope:F1}/Sample (max {maxSlopePerSample:F1}), " +
            $"Δ={ys[^1] - ys[0]:F0} über {samples.Count} Samples → {(hasDrift ? "DRIFT" : "stabil")}.";
        return new TrendResult(hasDrift, detail);
    }

    /// <summary>Ordinary-Least-Squares-Steigung über x = 0..n-1. Liefert 0 bei &lt; 2 Werten.</summary>
    public static double LeastSquaresSlope(IReadOnlyList<double> ys)
    {
        var n = ys.Count;
        if (n < 2) return 0d;

        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (var i = 0; i < n; i++)
        {
            double x = i;
            sumX += x;
            sumY += ys[i];
            sumXX += x * x;
            sumXY += x * ys[i];
        }

        var denom = n * sumXX - sumX * sumX;
        return denom == 0d ? 0d : (n * sumXY - sumX * sumY) / denom;
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
