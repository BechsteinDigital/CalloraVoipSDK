namespace CalloraVoipSdk.InteropHarness.Diagnostics;

/// <summary>Formatiert eine Menge von <see cref="SoakFailure"/> zu einer lesbaren Assert-Meldung.</summary>
public static class SoakFailureReport
{
    /// <summary>
    /// Erzeugt eine mehrzeilige Zusammenfassung (Kopfzeile mit Anzahl + eine Zeile je Fehlschlag).
    /// Leere Eingabe → knappe „keine Fehlschläge"-Zeile.
    /// </summary>
    public static string Describe(IReadOnlyCollection<SoakFailure> failures)
    {
        if (failures.Count == 0)
            return "Keine fehlgeschlagenen Vorgänge.";

        var lines = failures
            .OrderBy(f => f.Wave).ThenBy(f => f.Index)
            .Select(f =>
                $"  Welle {f.Wave} #{f.Index} Ports {f.PortPair} nach {f.Elapsed.TotalMilliseconds:F0} ms: " +
                $"{f.ExceptionType} — {f.Message}");

        return $"{failures.Count} fehlgeschlagene Vorgänge:\n" + string.Join("\n", lines);
    }
}
