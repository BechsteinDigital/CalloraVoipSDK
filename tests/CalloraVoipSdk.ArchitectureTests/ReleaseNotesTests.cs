using System.Text.RegularExpressions;
using Xunit;

namespace CalloraVoipSdk.ArchitectureTests;

/// <summary>
/// The release notes have to hold together: every link resolves, every file is listed, every version has
/// a changelog entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>This gate exists because of a failure it would have caught.</b> Moving the notes out of the
/// repository root into <c>docs/release-notes/</c> was a one-line change per file and broke fifteen
/// relative links at once — every <c>](CHANGELOG.md)</c> now pointed at a file one directory up, and
/// three ADR links pointed at <c>docs/adr/</c> from inside <c>docs/</c>. Nothing went red, because
/// nothing was looking. A reader following one of them landed on a 404 on GitHub.
/// </para>
/// <para>
/// The three checks are separable on purpose, so a failure says which shape of rot happened: a link that
/// no longer resolves, a note nobody can find from the index, or a version described in prose that the
/// machine-readable changelog does not know about.
/// </para>
/// </remarks>
public sealed class ReleaseNotesTests
{
    private static readonly string NotesDirectory =
        Path.Combine(SourceScan.RepoRoot, "docs", "release-notes");

    private static readonly Regex MarkdownLink = new(@"\[[^\]]*\]\(([^)\s]+)\)", RegexOptions.Compiled);

    // A version file, not the index: 4.15.0.md yes, README.md no.
    private static readonly Regex VersionFileName = new(@"^\d+\.\d+\.\d+\.md$", RegexOptions.Compiled);

    [Fact]
    public void Every_relative_link_in_a_release_note_resolves()
    {
        var broken = NoteFiles()
            .SelectMany(file => LinksOutsideCodeFences(file)
                .Where(link => !IsAbsolute(link.Target))
                .Where(link => !File.Exists(Resolve(link.Target)) && !Directory.Exists(Resolve(link.Target)))
                .Select(link => $"{Path.GetFileName(file)}:{link.Line} → {link.Target}"))
            .ToList();

        Assert.True(
            broken.Count == 0,
            $"""
             These links in docs/release-notes/ point at nothing:

               {string.Join("\n  ", broken)}

             They are resolved relative to docs/release-notes/, which is what a reader's browser does on
             GitHub. A link to the repository root needs ../../ in front of it.
             """);
    }

    [Fact]
    public void Every_release_note_is_listed_in_the_index_and_every_listed_note_exists()
    {
        var index = File.ReadAllText(Path.Combine(NotesDirectory, "README.md"));

        var onDisk = NoteFiles()
            .Select(Path.GetFileName)
            .Where(name => VersionFileName.IsMatch(name!))
            .ToHashSet(StringComparer.Ordinal);

        var listed = MarkdownLink.Matches(index)
            .Select(match => match.Groups[1].Value)
            .Where(target => VersionFileName.IsMatch(target))
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = onDisk.Except(listed).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missing = listed.Except(onDisk).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(
            unlisted.Count == 0 && missing.Count == 0,
            $"""
             docs/release-notes/README.md and the directory disagree.

             Written but not listed (nobody finds them): {Describe(unlisted)}
             Listed but not written (a dead row):        {Describe(missing)}

             The index is the only way into these files — a note that is not in the table is a note that
             was written for nobody.
             """);
    }

    [Fact]
    public void Every_release_note_has_a_changelog_entry_for_its_version()
    {
        var changelog = File.ReadAllText(Path.Combine(SourceScan.RepoRoot, "CHANGELOG.md"));

        var undocumented = NoteFiles()
            .Select(file => Path.GetFileNameWithoutExtension(file)!)
            .Where(name => VersionFileName.IsMatch(name + ".md"))
            .Where(version => !changelog.Contains($"## [{version}]", StringComparison.Ordinal))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            $"""
             These versions have a release note but no CHANGELOG.md section: {Describe(undocumented)}

             The two answer different questions — the changelog says *whether* something changed, the
             note says *what it means for you* — and a version that only has the second one is invisible
             to everyone who reads the first.
             """);
    }

    private static IEnumerable<string> NoteFiles() =>
        Directory.EnumerateFiles(NotesDirectory, "*.md").OrderBy(f => f, StringComparer.Ordinal);

    private static string Resolve(string target) =>
        Path.GetFullPath(Path.Combine(NotesDirectory, target.Split('#')[0]));

    private static bool IsAbsolute(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith('#');

    // Code fences carry sample output and shell lines that can look like links. Checking them would
    // produce failures nobody can fix, which is how a gate gets switched off.
    private static IEnumerable<(int Line, string Target)> LinksOutsideCodeFences(string file)
    {
        var inFence = false;
        var lineNumber = 0;

        foreach (var line in File.ReadLines(file))
        {
            lineNumber++;

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence) continue;

            foreach (Match match in MarkdownLink.Matches(line))
            {
                yield return (lineNumber, match.Groups[1].Value);
            }
        }
    }

    private static string Describe(IReadOnlyCollection<string> items) =>
        items.Count == 0 ? "none" : string.Join(", ", items);
}
