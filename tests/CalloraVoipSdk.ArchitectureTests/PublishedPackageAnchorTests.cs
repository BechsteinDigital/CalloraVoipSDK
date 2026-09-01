using Xunit;
using System.Text.RegularExpressions;

namespace CalloraVoipSdk.ArchitectureTests;

/// <summary>
/// Every package that reaches nuget.org has to be covered by the public-API baseline.
/// </summary>
/// <remarks>
/// <para>
/// <b>This gate exists because of a gap it would have caught.</b> The three audio packages had shipped
/// for months without being anchored in <see cref="PublicApiSurfaceTests"/> — 109 public members whose
/// signatures could change in any direction with nothing going red. The reason was not carelessness:
/// the architecture test project did not <em>reference</em> those projects, so there was no type to
/// anchor and no way to notice. A baseline can only guard what its own project can compile against.
/// </para>
/// <para>
/// So the question this test asks is not "is the baseline correct" — that is
/// <see cref="PublicApiSurfaceTests"/>'s job — but "does the baseline know about everything we
/// publish". It reads the answer from the file that actually decides what gets published,
/// <c>.github/workflows/packages.yml</c>, rather than from a list somebody has to remember to update.
/// Add a seventh package and this fails until it is referenced and anchored.
/// </para>
/// </remarks>
public sealed class PublishedPackageAnchorTests
{
    private static readonly string WorkflowPath =
        Path.Combine(SourceScan.RepoRoot, ".github", "workflows", "packages.yml");

    [Fact]
    public void Every_published_package_with_code_is_anchored_in_the_public_api_baseline()
    {
        var packed = PackedProjects();
        Assert.NotEmpty(packed); // a parser that silently finds nothing would pass forever

        var anchored = PublicApiSurfaceTests.AnchoredAssemblyNames;

        var unanchored = packed
            .Where(project => HasCompilableSource(project.Directory))
            .Select(project => project.PackageId)
            .Where(name => !anchored.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unanchored.Count == 0,
            $"""
             These packages are published but not covered by the public-API baseline:

               {string.Join("\n  ", unanchored)}

             Their surface can change in any direction without this build going red. Fix it in two
             steps, in this order — the second is impossible without the first:

               1. Add a ProjectReference to the package's project in
                  tests/CalloraVoipSdk.ArchitectureTests/CalloraVoipSdk.ArchitectureTests.csproj
               2. Add one public type from it to PublicApiSurfaceTests.AssemblyAnchors, then refresh
                  the baseline with UPDATE_PUBLIC_API=1 and review the diff.

             A package with no source files of its own — a metapackage — is exempt automatically and
             needs neither step.
             """);
    }

    [Fact]
    public void Nothing_is_pushed_that_was_never_packed()
    {
        // The workflow packs by project path and pushes by file name, and the two lists are written
        // out separately. A push line for a package no pack step produces fails the release halfway
        // through, after some packages are already public — which cannot be taken back.
        var packed = PackedProjects().Select(project => project.PackageId).ToHashSet(StringComparer.Ordinal);
        var pushed = PushedPackageIds();

        Assert.NotEmpty(pushed);
        var orphans = pushed.Where(id => !packed.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();

        Assert.True(
            orphans.Count == 0,
            $"packages.yml pushes {string.Join(", ", orphans)} but never packs them.");
    }

    /// <summary>The projects <c>packages.yml</c> runs <c>dotnet pack</c> on, with their package ids.</summary>
    /// <remarks>
    /// The package id is the project file's name: <c>src/Directory.Build.props</c> sets
    /// <c>PackageId = $(MSBuildProjectName)</c> for every project under <c>src</c>, so the two cannot
    /// drift apart without that line changing too.
    /// </remarks>
    private static IReadOnlyList<(string PackageId, string Directory)> PackedProjects()
    {
        var workflow = File.ReadAllText(WorkflowPath);

        return
        [
            .. Regex.Matches(workflow, @"dotnet pack\s+(?<path>[\w./\\-]+\.csproj)")
                .Select(match => match.Groups["path"].Value.Replace('\\', '/'))
                .Distinct(StringComparer.Ordinal)
                .Select(path => (
                    PackageId: Path.GetFileNameWithoutExtension(path),
                    Directory: Path.Combine(SourceScan.RepoRoot, Path.GetDirectoryName(path)!)))
        ];
    }

    /// <summary>The package ids <c>packages.yml</c> pushes to nuget.org.</summary>
    private static IReadOnlyCollection<string> PushedPackageIds()
    {
        var workflow = File.ReadAllText(WorkflowPath);

        return Regex.Matches(workflow, @"nuget push\s+""artifacts/nuget/(?<id>[\w.]+?)\.\$\{")
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a project carries code of its own, rather than being a metapackage of references.
    /// </summary>
    /// <remarks>
    /// Derived from the file system rather than from a list of known metapackages, and that is the
    /// point: a hardcoded exemption is the same trap one level up. The day somebody puts a class into
    /// the facade project, it stops being exempt on its own.
    /// </remarks>
    private static bool HasCompilableSource(string projectDirectory) =>
        Directory.Exists(projectDirectory)
        && Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Any(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
