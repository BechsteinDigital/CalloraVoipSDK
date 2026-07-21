namespace CalloraVoipSdk.InteropHarness.Diagnostics;

/// <summary>
/// Strukturierte Diagnose eines einzelnen fehlgeschlagenen Soak-Vorgangs (z. B. ein Round-Trip
/// in einer Concurrency-Welle). Ersetzt das frühere stille <c>catch → false</c>, das nur einen
/// Zähler übrig ließ: hier bleibt erhalten, WAS wo wann fehlschlug — auswertbar im nightly-Log
/// und wiederverwendbar für den strukturierten Audit-Sink.
/// </summary>
/// <param name="Wave">Wellen-Index (Concurrency-Soak); -1 für nicht gemessene Warm-up-Wellen.</param>
/// <param name="Index">Index des parallelen Vorgangs innerhalb der Welle.</param>
/// <param name="PortPair">Belegtes Loopback-Portpaar (z. B. "51824↔51825").</param>
/// <param name="Elapsed">Verstrichene Zeit bis zum Fehlschlag.</param>
/// <param name="ExceptionType">Typname der Ausnahme bzw. Fehlerklasse (z. B. "LengthMismatch").</param>
/// <param name="Message">Fehlermeldung / Kurzbeschreibung.</param>
public readonly record struct SoakFailure(
    int Wave,
    int Index,
    string PortPair,
    TimeSpan Elapsed,
    string ExceptionType,
    string Message);
