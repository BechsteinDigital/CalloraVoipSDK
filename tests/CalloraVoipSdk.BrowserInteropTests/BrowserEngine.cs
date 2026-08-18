using Microsoft.Playwright;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Ein Playwright-Browser-Motor (Chromium, Firefox, WebKit) für die browser-agnostische
/// WebRTC-Interop-Matrix — das Gegenstück zu <c>IPbxFixture</c> für die PBX-Matrix. Kapselt
/// die zwei Dinge, die sich zwischen den Browsern unterscheiden: die Auflösung des Executables
/// im ms-playwright-Cache und die Launch-Konfiguration. Chromium steuert Fake-Media und mDNS über
/// <c>--flags</c>, Firefox über <c>about:config</c>-UserPrefs — dieselbe <c>peer.html</c> (Standard-
/// <c>RTCPeerConnection</c>) läuft in beiden.
/// </summary>
public sealed class BrowserEngine
{
    private readonly string _cachePrefix;
    private readonly string[][] _executableRelativePaths;
    private readonly Func<IPlaywright, IBrowserType> _type;
    private readonly Func<string, BrowserTypeLaunchOptions> _options;

    private BrowserEngine(
        string name,
        string cachePrefix,
        string[][] executableRelativePaths,
        Func<IPlaywright, IBrowserType> type,
        Func<string, BrowserTypeLaunchOptions> options)
    {
        Name = name;
        _cachePrefix = cachePrefix;
        _executableRelativePaths = executableRelativePaths;
        _type = type;
        _options = options;
    }

    /// <summary>Anzeigename (chromium/firefox/webkit) für Test-Namen und Skip-Meldungen.</summary>
    public string Name { get; }

    /// <summary>Der aufgelöste Executable-Pfad aus dem ms-playwright-Cache, oder null wenn nicht installiert.</summary>
    public string? Executable => ResolveExecutable();

    /// <summary>Ob dieser Motor lokal installiert ist — steuert den Discovery-Zeit-Skip der Tests.</summary>
    public bool IsAvailable => Executable is not null;

    public override string ToString() => Name;

    /// <summary>
    /// Startet einen headless-Browser dieses Motors mit synthetischer Media (fake-device) und
    /// deaktiviertem mDNS (echte host-IP-Candidates, die die SDK-Fassade nicht als <c>.local</c> verwirft).
    /// </summary>
    public async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        var exe = Executable
            ?? throw new InvalidOperationException($"{Name} ist nicht im Playwright-Cache installiert.");
        return await _type(playwright).LaunchAsync(_options(exe));
    }

    private string? ResolveExecutable()
    {
        var root = BrowsersRoot();
        if (!Directory.Exists(root)) return null;

        // Neueste <prefix>-<rev>/… (höchste Revision zuerst). Innerhalb einer Revision werden mehrere
        // Kandidat-Layouts probiert, weil Playwright den Chromium-Ordner zwischen Versionen umbenannt hat
        // (chrome-linux ↔ chrome-linux64) — ohne das führt ein Layout-Unterschied im offiziellen Container
        // zu einem stillen Skip statt zu einem Lauf.
        foreach (var dir in Directory.GetDirectories(root, _cachePrefix + "-*").OrderByDescending(d => d))
        {
            foreach (var relative in _executableRelativePaths)
            {
                var exe = Path.Combine([dir, .. relative]);
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    /// <summary>
    /// Wurzel des Playwright-Browser-Caches: die von <c>PLAYWRIGHT_BROWSERS_PATH</c> gesetzte, sonst der
    /// per-User-Standardcache. Der offizielle Playwright-Container setzt die Variable auf <c>/ms-playwright</c>
    /// und liefert die Browser dort vorinstalliert, sodass die CI keinen apt-Download mehr braucht.
    /// </summary>
    private static string BrowsersRoot()
    {
        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache", "ms-playwright");
    }

    // ---------------------------------------------------------------------
    // Motoren
    // ---------------------------------------------------------------------

    public static readonly BrowserEngine Chromium = new(
        "chromium", "chromium", [["chrome-linux64", "chrome"], ["chrome-linux", "chrome"]],
        pw => pw.Chromium,
        exe => new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = exe,
            // Ohne das crasht Chromium als root ("Running as root without --no-sandbox is not supported") —
            // genau der Fall im offiziellen Playwright-Container, der als root läuft. ChromiumSandbox=false
            // lässt Playwright --no-sandbox durchreichen; für ein Test-Harness mit synthetischer Media ohne
            // Wirkung, und lokal (non-root) verhaltensneutral.
            ChromiumSandbox = false,
            Args =
            [
                "--use-fake-device-for-media-stream",            // synthetischer Audio/Video-Stream (kein Mikrofon)
                "--use-fake-ui-for-media-stream",                // getUserMedia auto-grant
                "--disable-features=WebRtcHideLocalIpsWithMdns", // echte host-IPs statt .local (SDK droppt .local)
                "--autoplay-policy=no-user-gesture-required",
            ],
        });

    public static readonly BrowserEngine Firefox = new(
        "firefox", "firefox", [["firefox", "firefox"]],
        pw => pw.Firefox,
        exe => new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = exe,
            // Firefox steuert dieselben Dinge wie die Chromium-Flags über about:config-Präferenzen.
            FirefoxUserPrefs = new Dictionary<string, object>
            {
                ["media.navigator.streams.fake"] = true,                        // fake A/V statt echtem Gerät
                ["media.navigator.permission.disabled"] = true,                 // getUserMedia auto-grant
                ["media.peerconnection.ice.obfuscate_host_addresses"] = false,  // echte host-IPs statt .local (mDNS off)
                // Erlaubt Firefox, Loopback-Candidates vom Remote zu AKZEPTIEREN (es generiert selbst keine) —
                // greift nur, falls InteropNetwork auf den 127.0.0.1-Fallback zurückfällt; sonst wirkungslos.
                ["media.peerconnection.ice.loopback"] = true,
                ["media.autoplay.default"] = 0,                                 // autoplay ohne User-Geste
                ["media.autoplay.blocking_policy"] = 0,
            },
        });

    public static readonly BrowserEngine WebKit = new(
        "webkit", "webkit", [["pw_run.sh"]],
        pw => pw.Webkit,
        exe => new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = exe,
            // WebKit (Linux-Playwright-Build) steuert Fake-Media über den BrowserContext, nicht über Launch-Flags;
            // opt-in — die Matrix skippt ihn, solange er nicht im Cache liegt.
        });

    /// <summary>Alle Motoren der Matrix. Nicht-installierte werden pro Test zur Discovery-Zeit übersprungen.</summary>
    public static IReadOnlyList<BrowserEngine> All => [Chromium, Firefox, WebKit];
}
