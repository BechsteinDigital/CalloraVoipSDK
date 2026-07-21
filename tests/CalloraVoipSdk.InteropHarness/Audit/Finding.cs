namespace CalloraVoipSdk.InteropHarness.Audit;

/// <summary>Ein Eintrag im Interop-/Soak-Audit-Register (eine Tabellenzeile).</summary>
/// <param name="Fid">Fortlaufende Finding-ID (z. B. F002).</param>
/// <param name="Type">Finding-Typ (Interop-Abweichung, Soak-Leak, Media-Defekt, Wire-Robustheit, Facade-Coupling-Gap).</param>
/// <param name="Evidence">Reproduzierender Test bzw. Gegenstelle.</param>
/// <param name="Symptom">Beobachtetes Verhalten.</param>
/// <param name="RootCause">Fehlerquelle/Root-Cause-Kategorie.</param>
/// <param name="Location">Betroffene <c>Datei:Zeile</c> im SDK.</param>
/// <param name="FixProposal">Vorgeschlagener Fix — <b>nicht ausgeführt</b>.</param>
/// <param name="Severity">Schweregrad.</param>
/// <param name="Status">Bearbeitungsstatus.</param>
public readonly record struct Finding(
    string Fid,
    string Type,
    string Evidence,
    string Symptom,
    string RootCause,
    string Location,
    string FixProposal,
    string Severity,
    string Status);
