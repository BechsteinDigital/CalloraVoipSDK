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
        var (fileDescriptors, sockets) = ReadDescriptorCounts();
        return new ResourceSample(
            SampleIndex: Interlocked.Increment(ref _index) - 1,
            ManagedBytes: GC.GetTotalMemory(forceFullCollection: false),
            PrivateMemoryBytes: process.PrivateMemorySize64,
            WorkingSetBytes: process.WorkingSet64,
            ThreadCount: process.Threads.Count,
            HandleCount: process.HandleCount,
            FileDescriptorCount: fileDescriptors,
            SocketDescriptorCount: sockets);
    }

    /// <summary>
    /// Zählt offene File-Descriptors und davon Sockets über <c>/proc/self/fd</c> (nur Linux).
    /// Jeder Eintrag ist ein Symlink; ein Ziel, das mit <c>socket:</c> beginnt, ist ein Socket.
    /// Außerhalb Linux gibt es kein <c>/proc</c> → Sentinel <c>(-1, -1)</c> (nicht erfassbar).
    /// </summary>
    private static (int fileDescriptors, int sockets) ReadDescriptorCounts()
    {
        if (!OperatingSystem.IsLinux())
            return (-1, -1);

        try
        {
            var fileDescriptors = 0;
            var sockets = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                fileDescriptors++;
                try
                {
                    // LinkTarget liefert das rohe Symlink-Ziel (z. B. "socket:[12345]"),
                    // ohne es als Pfad aufzulösen (die magischen /proc-Ziele sind keine echten Pfade).
                    var target = new FileInfo(entry).LinkTarget;
                    if (target is not null && target.StartsWith("socket:", StringComparison.Ordinal))
                        sockets++;
                }
                catch (IOException) { /* FD zwischen Enumeration und Read geschlossen — ignorieren */ }
                catch (UnauthorizedAccessException) { }
            }

            // Die Enumeration hält selbst einen Verzeichnis-FD auf /proc/self/fd offen, der als
            // Eintrag mitgezählt wird → um 1 bereinigen, damit die gemeldete Zahl der echten
            // Prozess-FD-Anzahl entspricht (relevant für das Messreihen-Artefakt; für Plateau/Slope
            // kürzt sich der konstante Offset ohnehin heraus).
            return (Math.Max(0, fileDescriptors - 1), sockets);
        }
        catch (IOException)
        {
            return (-1, -1);
        }
        catch (UnauthorizedAccessException)
        {
            return (-1, -1);
        }
    }
}
