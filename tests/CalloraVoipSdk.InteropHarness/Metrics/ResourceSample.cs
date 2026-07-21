namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Eine Momentaufnahme prozessweiter Ressourcenzähler zu einem Soak-Zeitpunkt.</summary>
/// <param name="SampleIndex">Fortlaufender Index innerhalb einer Soak-Serie.</param>
/// <param name="ManagedBytes">Verwalteter Heap in Bytes (<see cref="GC.GetTotalMemory(bool)"/>).</param>
/// <param name="PrivateMemoryBytes">Privater (committeter) Prozessspeicher — <see cref="System.Diagnostics.Process.PrivateMemorySize64"/>.
/// Erfasst auch nativen Verbrauch (Socket-Puffer, native Handles) und ist damit ein schärferes Leak-Signal als der Managed-Heap allein.</param>
/// <param name="WorkingSetBytes">Resident Working-Set — <see cref="System.Diagnostics.Process.WorkingSet64"/>.
/// Vom OS gesteuert und volatil; als Spitzenwert dokumentiert, nicht als scharfe Drift-Metrik gewertet.</param>
/// <param name="ThreadCount">Prozess-Threadanzahl (<see cref="System.Diagnostics.ProcessThreadCollection.Count"/>).</param>
/// <param name="HandleCount">Betriebssystem-Handle-Anzahl. Hinweis: liefert auf Linux stets 0 (nicht unterstützt) — dort <see cref="FileDescriptorCount"/> verwenden.</param>
/// <param name="FileDescriptorCount">Anzahl offener File-Descriptors (Linux: Einträge in <c>/proc/self/fd</c>). Sentinel <c>-1</c> außerhalb Linux (nicht erfassbar).</param>
/// <param name="SocketDescriptorCount">Anzahl offener Socket-Descriptors (Linux: <c>socket:</c>-Symlinks in <c>/proc/self/fd</c>). Sentinel <c>-1</c> außerhalb Linux.</param>
public readonly record struct ResourceSample(
    int SampleIndex,
    long ManagedBytes,
    long PrivateMemoryBytes,
    long WorkingSetBytes,
    int ThreadCount,
    int HandleCount,
    int FileDescriptorCount,
    int SocketDescriptorCount);
