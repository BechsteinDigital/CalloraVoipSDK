using System.Diagnostics;
using System.Threading;

namespace CalloraVoipSdk.InteropHarness.Metrics;

/// <summary>Erfasst prozessweite Ressourcenzähler als <see cref="ResourceSample"/>.</summary>
public sealed class ResourceSampler
{
    private int _index;

    /// <summary>
    /// Nimmt eine Momentaufnahme. <paramref name="forceGc"/> erzwingt eine vollständige
    /// Collection vor der Speichermessung, damit nur nicht mehr erreichbarer Heap als Sockel zählt.
    /// Nur für Baseline-/Intervall-Sockelmessungen mit <c>true</c> verwenden; bei hochfrequentem
    /// fortlaufendem Sampling <c>false</c> übergeben, sonst misst man den GC statt des Systems.
    /// </summary>
    public ResourceSample Capture(bool forceGc = true)
    {
        if (forceGc)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        using var process = Process.GetCurrentProcess();
        return new ResourceSample(
            SampleIndex: Interlocked.Increment(ref _index) - 1,
            ManagedBytes: GC.GetTotalMemory(forceFullCollection: false),
            ThreadCount: process.Threads.Count,
            HandleCount: process.HandleCount);
    }
}
