using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Basis für die per-Browser-Facts der Interop-Matrix: überspringt den Test zur Discovery-Zeit, wenn der
/// zugehörige <see cref="BrowserEngine"/> nicht im Playwright-Cache liegt. xUnit 2.4.2 kennt kein
/// Laufzeit-<c>Assert.Skip</c>, deshalb entscheidet das Attribut (nicht der Test-Body) über den Skip —
/// so erscheint ein fehlender Browser sauber als „skipped", nicht als grün-ohne-Assertion.
/// </summary>
public abstract class BrowserFactAttribute : FactAttribute
{
    protected BrowserFactAttribute(BrowserEngine engine)
    {
        if (!engine.IsAvailable)
            Skip = $"{engine.Name} nicht im Playwright-Cache (~/.cache/ms-playwright/{engine.Name}-*) — Interop-Test übersprungen.";
    }
}

/// <summary>Interop-Test gegen headless Chromium (übersprungen, wenn nicht installiert).</summary>
public sealed class ChromiumFactAttribute() : BrowserFactAttribute(BrowserEngine.Chromium);

/// <summary>Interop-Test gegen headless Firefox (übersprungen, wenn nicht installiert).</summary>
public sealed class FirefoxFactAttribute() : BrowserFactAttribute(BrowserEngine.Firefox);

/// <summary>
/// Wie <see cref="BrowserFactAttribute"/>, aber für parametrisierte Läufe — dieselbe Skip-Entscheidung zur
/// Discovery-Zeit, damit ein fehlender Browser auch bei einer Theory sauber als „skipped" erscheint.
/// </summary>
public abstract class BrowserTheoryAttribute : TheoryAttribute
{
    protected BrowserTheoryAttribute(BrowserEngine engine)
    {
        if (!engine.IsAvailable)
            Skip = $"{engine.Name} nicht im Playwright-Cache (~/.cache/ms-playwright/{engine.Name}-*) — Interop-Test übersprungen.";
    }
}

/// <summary>Parametrisierter Interop-Test gegen headless Chromium (übersprungen, wenn nicht installiert).</summary>
public sealed class ChromiumTheoryAttribute() : BrowserTheoryAttribute(BrowserEngine.Chromium);

/// <summary>Interop-Test gegen headless WebKit (übersprungen, wenn nicht installiert).</summary>
public sealed class WebKitFactAttribute() : BrowserFactAttribute(BrowserEngine.WebKit);
