using System.Text.RegularExpressions;

namespace CalloraVoipSdk.ArchitectureTests;

/// <summary>
/// Hilfsfunktionen fuer quelltextbasierte Architektur-Checks: Repo-Wurzel finden,
/// Quelldateien aufzaehlen und Baseline-Vergleiche mit klaren Fehlermeldungen ausfuehren.
/// </summary>
internal static class SourceScan
{
    private static readonly Lazy<string> LazyRepoRoot = new(LocateRepoRoot);

    /// <summary>Absoluter Pfad der Repo-Wurzel (Ordner mit CalloraVoipSdk.sln).</summary>
    public static string RepoRoot => LazyRepoRoot.Value;

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CalloraVoipSdk.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo-Wurzel (CalloraVoipSdk.sln) nicht gefunden.");
    }

    /// <summary>Alle .cs-Dateien unterhalb der angegebenen Repo-Unterordner, ohne obj/bin.</summary>
    public static IEnumerable<string> CsFiles(params string[] relativeRoots)
    {
        foreach (var relativeRoot in relativeRoots)
        {
            var root = Path.Combine(RepoRoot, relativeRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = Normalize(file);
                if (normalized.Contains("/obj/", StringComparison.Ordinal) ||
                    normalized.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>Repo-relativer Pfad mit '/'-Trennern.</summary>
    public static string Relative(string absolutePath)
        => Normalize(Path.GetRelativePath(RepoRoot, absolutePath));

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Erste per Regex gefundene Namespace-Deklaration der Datei oder null.</summary>
    public static string? DeclaredNamespace(string fileContent)
    {
        var match = Regex.Match(fileContent, @"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure"];

    /// <summary>
    /// Prueft, ob eine Datei ihr Schicht-Segment (Domain/Application/Infrastructure) korrekt im
    /// Namespace fuehrt. Verstoss ist sowohl ein FREMDES Schicht-Segment als auch das FEHLEN des
    /// eigenen (Layer-Omission, wie das fruehere Core.Security unter Domain/Security/). Dateien
    /// ausserhalb der drei Schichtordner sind nicht betroffen.
    /// </summary>
    public static bool LayerSegmentViolation(string relativePath, string declaredNamespace)
    {
        var folderLayer = Layers.FirstOrDefault(l => relativePath.Contains($"/{l}/", StringComparison.Ordinal));
        if (folderLayer is null)
        {
            return false;
        }

        var carriesForeignLayer = Layers.Any(l =>
            l != folderLayer &&
            Regex.IsMatch(declaredNamespace, $@"(^|\.){l}(\.|$)"));
        var ownLayerMissing = !Regex.IsMatch(declaredNamespace, $@"(^|\.){folderLayer}(\.|$)");
        return carriesForeignLayer || ownLayerMissing;
    }

    /// <summary>
    /// Vergleicht Ist-Verstoesse gegen die Baseline. Schlaegt fehl bei neuen Verstoessen
    /// UND bei veralteten Baseline-Eintraegen (Baseline darf nur schrumpfen).
    /// </summary>
    public static void AssertMatchesBaseline(string ruleName, IReadOnlyCollection<string> violations, IReadOnlyCollection<string> baseline)
    {
        var current = violations.ToHashSet(StringComparer.Ordinal);
        var allowed = baseline.ToHashSet(StringComparer.Ordinal);

        var newViolations = current.Except(allowed).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var staleBaseline = allowed.Except(current).OrderBy(v => v, StringComparer.Ordinal).ToList();

        if (newViolations.Count == 0 && staleBaseline.Count == 0)
        {
            return;
        }

        var message = $"Regel '{ruleName}':";
        if (newViolations.Count > 0)
        {
            message += $"\n  NEUE Verstoesse (beheben, nicht baselinen):\n    {string.Join("\n    ", newViolations)}";
        }

        if (staleBaseline.Count > 0)
        {
            message += $"\n  Veraltete Baseline-Eintraege (aus Baseline entfernen — Fund ist behoben):\n    {string.Join("\n    ", staleBaseline)}";
        }

        Xunit.Assert.Fail(message);
    }
}
