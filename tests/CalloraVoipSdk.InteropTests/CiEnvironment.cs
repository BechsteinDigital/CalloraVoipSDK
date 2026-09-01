namespace CalloraVoipSdk.InteropTests;

/// <summary>Ob dieser Lauf in GitHub Actions stattfindet.</summary>
/// <remarks>
/// Bewusst <c>GITHUB_ACTIONS</c> und nicht das allgemeinere <c>CI</c>: Letzteres setzen auch lokale
/// Werkzeuge, und die Folge eines falsch positiven Treffers wäre ein rot laufender Entwicklungsrechner
/// ohne Docker — also genau die Bequemlichkeit, die das Überspringen herstellen soll. Kommt ein zweites
/// CI-System dazu, wird diese eine Stelle erweitert.
/// </remarks>
internal static class CiEnvironment
{
    public static bool IsGitHubActions =>
        string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
}
