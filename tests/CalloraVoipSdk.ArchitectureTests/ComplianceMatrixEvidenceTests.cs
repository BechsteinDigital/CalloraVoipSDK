using System.Text.RegularExpressions;
using Xunit;

namespace CalloraVoipSdk.ArchitectureTests;

/// <summary>
/// Keeps the RFC compliance matrix pointing at code that exists. Every row marked <c>Erledigt</c> that names a
/// backtick-quoted symbol as its evidence is checked: the symbol has to appear somewhere in <c>src/</c>, and a
/// row citing a test by name has to name a test that exists.
/// </summary>
/// <remarks>
/// <para>
/// This gate exists because the matrix drifted without anyone noticing. #285 uncovered a row recorded as done
/// whose named function had no caller, no test, and was wrong in five of the ten worked examples the RFC itself
/// publishes; the sweep that followed (#336) found four rows pointing at symbols that had been renamed away or at
/// tests that no longer existed. Nothing failed when that happened — which is exactly what makes documentation
/// rot: it never goes red.
/// </para>
/// <para>
/// What this can and cannot do: it proves the matrix names something real, not that the something real is
/// correct or covered. A row can pass here and still overstate. That weaker check is deliberate — it is the part
/// that can be decided mechanically, and a gate that guesses at coverage would either be ignored or gamed.
/// </para>
/// </remarks>
public sealed class ComplianceMatrixEvidenceTests
{
    private const string MatrixPath = "docs/archive/status-raw/RFC_VOIP_SDK_COMPLIANCE.md";

    // Rows whose named evidence is known to be missing and has not been resolved yet. Shrink-only, like the
    // other baselines here: a new drifted row fails, and a repaired one must be deleted from this list.
    private static readonly string[] MissingEvidenceBaseline = [];

    [Fact]
    public void Every_done_row_names_evidence_that_exists()
    {
        var repoRoot = RepoRoot();
        var matrix = Path.Combine(repoRoot, MatrixPath);
        Assert.True(File.Exists(matrix), $"Die Compliance-Matrix wurde nicht gefunden: {MatrixPath}");

        var sourceText = ReadAll(Path.Combine(repoRoot, "src"));
        var testText = ReadAll(Path.Combine(repoRoot, "tests"));

        var violations = new List<string>();
        var lines = File.ReadAllLines(matrix);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith('|'))
                continue;

            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4 || !cells.Any(c => c.Trim('*') == "Erledigt"))
                continue;

            foreach (var symbol in NamedSymbols(line))
            {
                // EVERY segment of a dotted name is checked, not just the last one. Checking only the leaf
                // lets a renamed type through whenever its member has a common name — `Foo.Create` would pass
                // on the strength of some unrelated `Create` elsewhere. Found by mutation: renaming the type
                // in a row did not go red until this looked at both halves.
                //
                // Searched in both trees rather than guessing which one a name belongs to: a row may cite
                // production code or a test as its evidence, and only absent from both is a finding.
                foreach (var part in symbol.Split('.'))
                {
                    if (!sourceText.Contains(part, StringComparison.Ordinal)
                        && !testText.Contains(part, StringComparison.Ordinal))
                    {
                        violations.Add($"{MatrixPath}:{i + 1} :: '{symbol}' ({cells[0]})");
                        break;
                    }
                }
            }
        }

        SourceScan.AssertMatchesBaseline(
            "Compliance-Matrix nennt existierende Belege", violations, MissingEvidenceBaseline);
    }

    // Rows whose named symbol exists but which no test mentions. Not a failure in itself — a behaviour can be
    // covered by a test that never names the type — but every entry here is a claim of "Erledigt" resting on
    // something no test points at, and the matrix's own bar is "umgesetzt, getestet". Shrink-only: work an entry
    // off by naming the covering test in the row (or by writing one), then delete the line.
    private static readonly string[] UnnamedByAnyTestBaseline =
    [
        "10.2 :: InstanceId",
        "11 :: HandleOptionsAsync",
        "13.3.1.4 :: SipServerTransactionEngine.ArmInviteSuccessRetransmit",
        "18.1.1 :: EscalateViaTransportToTcp",
        "18.4 :: SendPayloadAsync",
        "19.1.1 :: GetUriParam",
        "19.1.1 :: InferTransport",
        "19.1.5 :: InferTransport",
        "19.1.5 :: SipDnsRouteResolver",
        "19.2 :: SipRequireOptionPolicy",
        "26.1.2 :: SipTransportRuntime.InferTransport",
        "26.2.2 :: InferTransport",
        "26.2.2 :: SipDnsRouteResolver",
        "8.1.1.7 :: SipProtocol.ReflectViaRport",
        "8.2.2.1 :: ISipUasUserIdentityPolicy",
        "RFC 3551 :: SdpUtilities.DefaultCodecs",
        "RFC 4488 :: SupportedOptionTags",
        "RFC 6062 :: OpenTcpDataConnectionAsync",
        "RFC 6062 :: TurnTcpDataConnectionFactory",
        "RFC 6062 :: TurnTcpPassiveConnectionService",
        "RFC 8016 :: TurnMobilityTicketStore",
    ];

    /// <summary>
    /// Reports every <c>Erledigt</c> row whose named symbol no test mentions, against a shrink-only baseline.
    /// Weaker than the existence check above and deliberately so: naming is not coverage in either direction.
    /// What it buys is that the set cannot silently grow — a new row claiming done without a test that points at
    /// anything has to be argued for, in the baseline, in a diff someone reviews.
    /// </summary>
    [Fact]
    public void No_new_done_row_rests_on_a_symbol_no_test_mentions()
    {
        var repoRoot = RepoRoot();
        var sourceText = ReadAll(Path.Combine(repoRoot, "src"));
        var testText = ReadAll(Path.Combine(repoRoot, "tests"));

        var unnamed = new List<string>();
        foreach (var line in File.ReadAllLines(Path.Combine(repoRoot, MatrixPath)))
        {
            if (!line.StartsWith('|'))
                continue;

            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4 || !cells.Any(c => c.Trim('*') == "Erledigt"))
                continue;

            foreach (var symbol in NamedSymbols(line))
            {
                var leaf = symbol.Split('.')[^1];
                if (sourceText.Contains(leaf, StringComparison.Ordinal)
                    && !testText.Contains(leaf, StringComparison.Ordinal))
                {
                    unnamed.Add($"{cells[0]} :: {symbol}");
                }
            }
        }

        SourceScan.AssertMatchesBaseline(
            "Compliance-Zeile ohne Test, der ihr Symbol nennt", unnamed.Distinct().ToList(), UnnamedByAnyTestBaseline);
    }

    // Backtick-quoted identifiers that look like code: PascalCase, optionally dotted. Prose, file names and
    // lower-case tokens are ignored — the point is to check what the row offers as evidence, not to parse it.
    private static IEnumerable<string> NamedSymbols(string row) =>
        Regex.Matches(row, @"`([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*)`")
            .Select(m => m.Groups[1].Value)
            .Where(s => char.IsUpper(s[0]) && s.Length > 3 && !s.EndsWith(".cs", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

    private static string ReadAll(string directory)
    {
        // This file is skipped when reading the test tree: its own baseline lists the very symbol names being
        // searched for, so including it would make every entry look covered and the check would pass vacuously.
        // Found the hard way — the first run reported all 21 baseline entries as resolved at once.
        if (!Directory.Exists(directory))
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file) == $"{nameof(ComplianceMatrixEvidenceTests)}.cs")
            {
                continue;
            }

            builder.Append(File.ReadAllText(file));
        }

        return builder.ToString();
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CalloraVoipSdk.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository-Wurzel nicht gefunden.");
    }
}
