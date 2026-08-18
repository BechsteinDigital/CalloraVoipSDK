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
    /// relativen Floor (<paramref name="toleranceRatio"/> vom Startsockel).
    /// Erwartet nicht-negative, finite Werte (z. B. Jitter/RTT in ms); negative Startsockel brechen die
    /// "Aufwärts"-Semantik.
    /// <para>
    /// <paramref name="absoluteFloor"/> unterdrückt Fehlalarme bei einem Startsockel nahe 0: Drift wird nur
    /// gemeldet, wenn der Endsockel diesen absoluten Wert <em>überschreitet</em>. Auf sauberem Loopback ist
    /// Sub-Millisekunden-Jitter reines Scheduling-Rauschen — ein relativer +50 %-Sprung von 0,3 auf 0,7 ms
    /// ist kein Leak. Der Floor muss in denselben Einheiten wie die Metrik angegeben werden (z. B. ms).
    /// </para>
    /// </summary>
    /// <param name="samples">Chronologische Messreihe (mindestens 2 Werte).</param>
    /// <param name="selector">Extrahiert die zu prüfende Metrik.</param>
    /// <param name="toleranceRatio">Erlaubtes relatives Wachstum (z. B. 0.20 = 20 %).</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    /// <param name="absoluteFloor">
    /// Absoluter Mindest-Endwert, unterhalb dessen niemals Drift gemeldet wird (Standard 0 = kein Floor).
    /// </param>
    public static TrendResult NoUpwardDrift<T>(
        IReadOnlyList<T> samples,
        Func<T, double> selector,
        double toleranceRatio = 0.10,
        string metricName = "value",
        double absoluteFloor = 0d)
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
        // Drift nur, wenn der Endsockel sowohl relativ über der Schwelle als auch absolut über dem Floor liegt.
        var hasDrift = end > threshold && end > absoluteFloor;
        var floorNote = absoluteFloor > 0d ? $", Floor={absoluteFloor:F3}" : string.Empty;
        var detail =
            $"{metricName}: Start≈{start:F3}, Ende≈{end:F3}, Schwelle={threshold:F3}{floorNote} " +
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

    /// <summary>
    /// Prüft, ob die Metrik zwischen Anfang und Ende der Reihe um mehr als <paramref name="maxGrowth"/>
    /// gewachsen ist — Median des ersten gegen Median des letzten Fünftels, damit ein einzelner Ausreißer
    /// am Rand die Aussage nicht kippt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Für Metriken gedacht, die einen einmaligen Sockelaufbau tragen: der Prozess-Commit
    /// (<c>PrivateMemoryBytes</c>) wächst in Stufen, wenn Laufzeit, GC-Heap oder ThreadPool nachlegen, und
    /// gibt sie nicht zurück. Gemessene Reihe eines grünen Laufs, MB über dem Startwert:
    /// <c>0 0 0,1 0,2 0,6 0,6 0,7 2,0 2,9 5,2 5,2 5,3 5,3 5,3 7,4 7,4 7,4 7,4</c> — Stufen, dann flach.
    /// </para>
    /// <para>
    /// Eine Steigungsgrenze ist dafür die falsche Einheit: sie <em>ist</em> eine Gesamtwachstumsgrenze, die
    /// ihre eigene Höhe verschweigt und mit der Abtastrate wandert. 1 MB/Sample über 18 Samples heißt
    /// „höchstens 18 MB"; dieselben 24 MB über 36 Samples kämen unbeanstandet durch. Genau daran kippte das
    /// Chaos-Gate (#283): auf CI wuchs der Commit um 23,5 bzw. 24,1 MB, also über die versteckte Grenze,
    /// während lokal 7,4 MB anfielen — identischer Commit, beide Ergebnisse. Eine explizite Obergrenze sagt,
    /// was sie prüft.
    /// </para>
    /// <para>
    /// Die Aussage ist damit bewusst schwächer und ehrlicher: <b>grobe</b> Lecks werden gefangen (etwas, das
    /// pro Iteration nachlegt, sprengt jede sinnvolle Obergrenze), ein schleichendes natives Leck unterhalb
    /// der Grenze nicht. Die scharfen Leck-Detektoren bleiben <c>ManagedBytes</c>, <c>ThreadCount</c> und
    /// <c>SocketDescriptorCount</c>: die müssen deterministisch zurückgehen und behalten ihre enge Steigung.
    /// </para>
    /// </remarks>
    /// <param name="samples">Chronologische Messreihe (mindestens 2 Werte).</param>
    /// <param name="selector">Extrahiert die zu prüfende Metrik.</param>
    /// <param name="maxGrowth">Erlaubtes absolutes Wachstum in Metrik-Einheiten.</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    public static TrendResult NoAbsoluteGrowth(
        IReadOnlyList<ResourceSample> samples,
        Func<ResourceSample, double> selector,
        double maxGrowth,
        string metricName)
    {
        if (samples.Count < 2)
            return new TrendResult(false, $"{metricName}: zu wenige Samples ({samples.Count}).");

        var bucket = Math.Max(1, samples.Count / 5);
        var start = MedianOfDouble(samples.Take(bucket).Select(selector));
        var end = MedianOfDouble(samples.Skip(samples.Count - bucket).Select(selector));

        var growth = end - start;
        var hasDrift = growth > maxGrowth;
        var detail =
            $"{metricName}: Start≈{start:F0}, Ende≈{end:F0}, Wachstum={growth:F0} " +
            $"(max {maxGrowth:F0}) über {samples.Count} Samples → {(hasDrift ? "DRIFT" : "stabil")}.";
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
