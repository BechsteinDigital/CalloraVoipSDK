namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>
/// Absolute-Plateau-Prüfungen für Zähler-Ressourcen (Threads, Sockets, File-Descriptors).
/// Anders als der relative <see cref="TrendAssertions"/> erlaubt ein Plateau nur eine kleine,
/// <em>absolute</em> Toleranz über einer Baseline — die scharfe Signatur eines Handle-/Socket-Leaks
/// (jede nicht freigegebene Ressource hebt den Endsockel dauerhaft, unabhängig vom Ausgangsniveau).
/// Metriken, die auf der laufenden Plattform nicht erfassbar sind (Sentinel &lt; 0, z. B.
/// File-Descriptors außerhalb Linux), werden als <see cref="PlateauResult.Skipped"/> gemeldet.
/// </summary>
public static class ResourcePlateauAssertions
{
    /// <summary>
    /// Prüft, ob der Endsockel (Median des letzten Fünftels) der per <paramref name="selector"/>
    /// gewählten Zähler-Metrik die Baseline (Median des ersten Fünftels) um mehr als
    /// <paramref name="absoluteTolerance"/> überschreitet.
    /// </summary>
    /// <param name="samples">Chronologische, bereits warm gelaufene Messreihe.</param>
    /// <param name="selector">Extrahiert die zu prüfende Zähler-Metrik (Sentinel &lt; 0 ⇒ übersprungen).</param>
    /// <param name="absoluteTolerance">Erlaubter absoluter Zuwachs über die Baseline (z. B. 0 für Sockets, 1 für Threads).</param>
    /// <param name="metricName">Anzeigename der Metrik für die Begründung.</param>
    public static PlateauResult WithinPlateau(
        IReadOnlyList<ResourceSample> samples,
        Func<ResourceSample, int> selector,
        int absoluteTolerance,
        string metricName)
    {
        if (samples.Count == 0)
            return new PlateauResult(Exceeded: false, Skipped: true, $"{metricName}: keine Samples.");

        // Sentinel < 0 ⇒ Metrik auf dieser Plattform nicht erfasst → nicht werten.
        if (selector(samples[0]) < 0)
            return new PlateauResult(
                Exceeded: false, Skipped: true,
                $"{metricName}: auf dieser Plattform nicht erfasst — übersprungen.");

        var bucket = Math.Max(1, samples.Count / 5);
        var baseline = MedianInt(samples.Take(bucket).Select(selector));
        var end = MedianInt(samples.Skip(samples.Count - bucket).Select(selector));
        var ceiling = baseline + absoluteTolerance;
        var exceeded = end > ceiling;
        var detail =
            $"{metricName}: Baseline={baseline}, Ende={end}, Deckel={ceiling} " +
            $"(Baseline+{absoluteTolerance}) → {(exceeded ? "ÜBERSCHRITTEN" : "Plateau")}.";
        return new PlateauResult(exceeded, Skipped: false, detail);
    }

    private static int MedianInt(IEnumerable<int> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0;
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[mid]
            : (ordered[mid - 1] + ordered[mid]) / 2;
    }
}
