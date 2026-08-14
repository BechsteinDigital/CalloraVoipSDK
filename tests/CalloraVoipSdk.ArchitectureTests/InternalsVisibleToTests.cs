using System.Text.RegularExpressions;
using Xunit;

namespace CalloraVoipSdk.ArchitectureTests;

/// <summary>
/// <c>InternalsVisibleTo</c> oeffnet die internen Typen einer Assembly fuer eine andere, die
/// ausschliesslich ueber ihren <em>Namen</em> benannt wird. Die Assemblies dieses Repos sind nicht
/// signiert (kein <c>SignAssembly</c> in den Build-Props), der Name ist also die vollstaendige
/// Pruefung — ein Grant auf eine Assembly, die es nicht gibt, laesst sich von jeder DLL einloesen,
/// die sich so nennt.
///
/// Solche Grants entstehen still: ein Testprojekt wird umbenannt oder faellt weg, der Grant bleibt
/// stehen, und es faellt niemandem auf, weil nichts bricht. Genau deshalb dieser Test — er prueft
/// die Richtung, die der Compiler nicht prueft.
/// </summary>
public sealed class InternalsVisibleToTests
{
    private static readonly string[] ProjectRoots = ["src", "tests", "perf", "examples"];

    [Fact]
    public void Jeder_InternalsVisibleTo_Grant_zeigt_auf_eine_Assembly_die_dieses_Repo_baut()
    {
        var built = BuiltAssemblyNames();
        var grants = Grants();

        // Ein leerer Scan waere ein stiller Test, kein gruener: er wuerde jede Verrottung durchlassen.
        Assert.NotEmpty(grants);
        Assert.NotEmpty(built);

        var dangling = grants
            .Where(grant => !built.Contains(grant.Assembly))
            .Select(grant => $"{grant.Source}: \"{grant.Assembly}\"")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            dangling.Count == 0,
            "InternalsVisibleTo zeigt auf Assemblies, die dieses Repo nicht baut. Diese Assemblies sind\n"
            + "unsigniert, der Grant haengt also allein am Namen — eine fremde DLL mit demselben Namen\n"
            + "erhaelt Zugriff auf die Internals. Entweder den Grant entfernen oder den Namen korrigieren:\n    "
            + string.Join("\n    ", dangling));
    }

    /// <summary>Alle <c>InternalsVisibleTo</c>-Grants der Produktivassemblies, mit ihrer Fundstelle.</summary>
    private static IReadOnlyList<(string Source, string Assembly)> Grants()
    {
        var pattern = new Regex(
            """InternalsVisibleTo\s*\(\s*"(?<assembly>[^"]+)"\s*\)""",
            RegexOptions.Compiled);

        return SourceScan.CsFiles("src")
            .SelectMany(file => pattern
                .Matches(File.ReadAllText(file))
                .Select(match => (SourceScan.Relative(file), match.Groups["assembly"].Value)))
            .ToList();
    }

    /// <summary>
    /// Die Assembly-Namen, die dieses Repo tatsaechlich erzeugt: der Projektdateiname, sofern das
    /// Projekt ihn nicht per <c>AssemblyName</c> ueberschreibt.
    /// </summary>
    private static HashSet<string> BuiltAssemblyNames()
    {
        var assemblyName = new Regex(
            @"<AssemblyName>\s*(?<name>[^<\s]+)\s*</AssemblyName>",
            RegexOptions.Compiled);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in ProjectRoots)
        {
            var directory = Path.Combine(SourceScan.RepoRoot, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var project in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories))
            {
                var declared = assemblyName.Match(File.ReadAllText(project));
                names.Add(declared.Success
                    ? declared.Groups["name"].Value
                    : Path.GetFileNameWithoutExtension(project));
            }
        }

        return names;
    }
}
