namespace CalloraVoipSdk.SoakTests;

/// <summary>
/// Parametersatz für einen Soak-Lauf. <see cref="Short"/> liefert kleine, CI-taugliche Werte
/// (Smoke: läuft der Mechanismus?); <see cref="Long"/> die schweren Werte für den nightly-Lauf,
/// per Umgebungsvariable übersteuerbar (<c>SOAK_ITERATIONS</c>, <c>SOAK_WAVES</c>,
/// <c>SOAK_PARALLELISM</c>, <c>SOAK_DURATION_SECONDS</c>).
/// </summary>
/// <param name="Iterations">Anzahl serieller Zyklen (Leak-Soak).</param>
/// <param name="Waves">Anzahl paralleler Wellen (Concurrency-Soak).</param>
/// <param name="Parallelism">Parallele Loopbacks pro Welle.</param>
/// <param name="Duration">Lauf-Dauer (Media-/Signaling-Soak).</param>
public sealed record SoakProfile(int Iterations, int Waves, int Parallelism, TimeSpan Duration)
{
    /// <summary>Kleines PR-CI-Profil (Smoke) — schnell, deckt den Mechanismus ab, nicht die Leak-Tiefe.</summary>
    public static SoakProfile Short { get; } = new(
        Iterations: 20, Waves: 3, Parallelism: 5, Duration: TimeSpan.FromSeconds(3));

    /// <summary>Schweres nightly-Profil (echte Leak-/Drift-Tiefe), per Env-Var übersteuerbar.</summary>
    public static SoakProfile Long => new(
        Iterations: EnvInt("SOAK_ITERATIONS", 500),
        Waves: EnvInt("SOAK_WAVES", 20),
        Parallelism: EnvInt("SOAK_PARALLELISM", 25),
        Duration: TimeSpan.FromSeconds(EnvInt("SOAK_DURATION_SECONDS", 20)));

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
