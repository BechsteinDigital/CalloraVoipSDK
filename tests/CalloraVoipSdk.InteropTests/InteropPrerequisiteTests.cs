using Xunit;

namespace CalloraVoipSdk.InteropTests;

/// <summary>
/// In CI ist eine fehlende Voraussetzung kein Grund zum Überspringen, sondern ein Befund.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DockerRequiredFactAttribute"/> überspringt sich, wenn kein Docker-Daemon antwortet. Auf
/// einem Entwicklungsrechner ist das genau richtig — man soll die Testsuite ohne Docker fahren können.
/// In CI kehrt sich die Bedeutung um: Dort MUSS Docker da sein, und wenn nicht, meldet der Job
/// <b>grün, ohne irgendetwas geprüft zu haben</b>. Die zentrale Zusage der README — „läuft in jedem
/// CI-Lauf gegen ein echtes Asterisk" — wäre dann still unwahr, und niemand sähe es: ein grüner Haken
/// an einem Job, der null Tests ausgeführt hat, unterscheidet sich optisch durch nichts von einem, der
/// dreißig bestanden hat.
/// </para>
/// <para>
/// Dieser Test läuft immer und überspringt sich nie. Er macht aus dem stillen Nichtstun eine einzelne,
/// lesbare Fehlermeldung an der Stelle, an der die Ursache steht.
/// </para>
/// </remarks>
// Trägt die Kategorie des Jobs, in dem er wirken soll. Ohne sie filtert ihn genau der
// Lauf heraus, den er bewacht — ein Test, der da ist und nie läuft.
[Trait("Category", "Interop")]
public sealed class InteropPrerequisiteTests
{
    [Fact]
    public void A_docker_daemon_is_reachable_when_running_in_ci()
    {
        if (!CiEnvironment.IsGitHubActions)
        {
            // Lokal ist das Fehlen von Docker eine Entscheidung, keine Störung.
            return;
        }

        Assert.True(
            DockerRequiredFactAttribute.IsDockerAvailable,
            "Kein erreichbarer Docker-Daemon auf dem CI-Runner. Sämtliche Interop-Tests hätten sich "
            + "übersprungen und der Job wäre grün geworden, ohne ein einziges Mal gegen Asterisk "
            + "gelaufen zu sein. Der Runner ist defekt, nicht der Code.");
    }
}
