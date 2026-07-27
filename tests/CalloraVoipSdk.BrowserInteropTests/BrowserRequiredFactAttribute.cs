using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Ein <see cref="FactAttribute"/>, das den Test überspringt, wenn kein Playwright-Chromium auffindbar
/// ist (analog DockerRequiredFact). Exponiert den gefundenen Browser-Pfad für den Playwright-Launch.
/// </summary>
public sealed class BrowserRequiredFactAttribute : FactAttribute
{
    /// <summary>Der aufgelöste Chromium-Executable-Pfad, oder null wenn keiner gefunden wurde.</summary>
    public static readonly string? ChromiumPath = ResolveChromium();

    public BrowserRequiredFactAttribute()
    {
        if (ChromiumPath is null)
            Skip = "Kein Playwright-Chromium gefunden (~/.cache/ms-playwright/chromium-*) — Browser-Interop-Test übersprungen.";
    }

    private static string? ResolveChromium()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".cache", "ms-playwright");
        if (!Directory.Exists(root)) return null;
        // Neueste chromium-<rev>/chrome-linux64/chrome (höchste Revision zuerst).
        foreach (var dir in Directory.GetDirectories(root, "chromium-*").OrderByDescending(d => d))
        {
            var exe = Path.Combine(dir, "chrome-linux64", "chrome");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }
}
