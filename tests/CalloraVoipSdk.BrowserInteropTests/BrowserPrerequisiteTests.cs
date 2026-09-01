using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// In CI ist ein fehlender Browser kein Grund zum Überspringen, sondern ein defekter Runner.
/// </summary>
/// <remarks>
/// <para>
/// Dieselbe Umkehrung wie bei Docker: <see cref="BrowserFactAttribute"/> überspringt, wenn ein Motor
/// nicht im Playwright-Cache liegt — lokal richtig, in CI fatal. Der Job läuft im offiziellen
/// Playwright-Container, dort MÜSSEN Chromium und Firefox vorhanden sein. Wären sie es nicht, meldete
/// <c>browser-interop</c> grün, ohne dass je ein Angebot, eine Antwort, ein DTLS-Handshake oder ein
/// Medienpaket geprüft worden wäre — und die README-Zusage „gegen echte Browser" wäre still unwahr.
/// </para>
/// <para>
/// <b>WebKit steht bewusst nicht in dieser Prüfung.</b> Von WebKit läuft genau ein Test, und der
/// startet den Browser. Ihn hier zu fordern hieße, eine Zusage zu erzwingen, die das Projekt nicht
/// macht.
/// </para>
/// </remarks>
// Trägt die Kategorie des Jobs, in dem er wirken soll. Ohne sie filtert ihn genau der
// Lauf heraus, den er bewacht — ein Test, der da ist und nie läuft.
[Trait("Category", "BrowserInterop")]
public sealed class BrowserPrerequisiteTests
{
    [Fact]
    public void The_engines_the_webrtc_matrix_needs_are_installed_when_running_in_ci()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            // Lokal darf man die Suite ohne installierte Browser fahren. Die Bedingung steht hier
            // ausgeschrieben statt in einem geteilten Helfer: die beiden Interop-Projekte teilen kein
            // gemeinsames Testprojekt, und ein Link-Include quer über Projektgrenzen wäre mehr Bauwerk
            // als die eine Zeile wert ist, die er einspart.
            return;
        }

        var missing = new[] { BrowserEngine.Chromium, BrowserEngine.Firefox }
            .Where(engine => !engine.IsAvailable)
            .Select(engine => engine.Name)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Nicht im Playwright-Cache auf dem CI-Runner: {string.Join(", ", missing)}. "
            + "Sämtliche WebRTC-Browser-Tests dieser Motoren hätten sich übersprungen und der Job wäre "
            + "grün geworden, ohne einen einzigen Handshake geprüft zu haben. Der Container oder "
            + "PLAYWRIGHT_BROWSERS_PATH stimmt nicht — nicht der Code.");
    }
}
