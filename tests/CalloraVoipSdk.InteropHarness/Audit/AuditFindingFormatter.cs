namespace CalloraVoipSdk.InteropHarness.Audit;

/// <summary>Rendert <see cref="Finding"/>s als Markdown-Tabellenzeilen fürs Audit-Register.</summary>
public static class AuditFindingFormatter
{
    /// <summary>Formatiert ein Finding als eine einzelne Markdown-Tabellenzeile.</summary>
    public static string ToMarkdownRow(Finding f) =>
        "| " + string.Join(" | ", new[]
        {
            Cell(f.Fid), Cell(f.Type), Cell(f.Evidence), Cell(f.Symptom),
            Cell(f.RootCause), Cell(f.Location), Cell(f.FixProposal),
            Cell(f.Severity), Cell(f.Status),
        }) + " |";

    private static string Cell(string value) =>
        (value ?? string.Empty).Replace("|", @"\|").Replace("\r", " ").Replace("\n", " ");
}
