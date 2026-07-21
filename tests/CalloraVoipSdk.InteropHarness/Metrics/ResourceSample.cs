namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Eine Momentaufnahme prozessweiter Ressourcenzähler zu einem Soak-Zeitpunkt.</summary>
/// <param name="SampleIndex">Fortlaufender Index innerhalb einer Soak-Serie.</param>
/// <param name="ManagedBytes">Verwalteter Heap in Bytes (<see cref="GC.GetTotalMemory(bool)"/>).</param>
/// <param name="ThreadCount">Prozess-Threadanzahl.</param>
/// <param name="HandleCount">Betriebssystem-Handle-Anzahl des Prozesses. Hinweis: liefert auf Linux stets 0 (nicht unterstützt) — dort nicht als Leak-Metrik verwenden.</param>
public readonly record struct ResourceSample(
    int SampleIndex,
    long ManagedBytes,
    int ThreadCount,
    int HandleCount);
