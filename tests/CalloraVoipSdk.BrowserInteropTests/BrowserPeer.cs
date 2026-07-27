using Microsoft.Playwright;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Ein echter headless-Chromium-Peer (Playwright), der die von der Signaling-Bridge servierte
/// <c>peer.html</c> lädt und als WebRTC-Answerer gegen die SDK-Fassade connectet. Startet mit
/// synthetischer Media (fake-device) und deaktiviertem mDNS (echte host-IP-Candidates).
/// </summary>
public sealed class BrowserPeer : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>Gesammelte Browser-Konsolen-/Fehler-Meldungen (Diagnose bei Fehlschlag).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Logs { get; } = new();

    public async Task NavigateAsync(string url)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = BrowserRequiredFactAttribute.ChromiumPath,
            Args =
            [
                "--use-fake-device-for-media-stream",            // synthetischer Audio/Video-Stream (kein Mikrofon)
                "--use-fake-ui-for-media-stream",                // getUserMedia auto-grant
                "--disable-features=WebRtcHideLocalIpsWithMdns", // echte host-IPs statt .local (SDK droppt .local)
                "--autoplay-policy=no-user-gesture-required",
            ],
        });
        var page = await _browser.NewPageAsync();
        page.Console += (_, m) => Logs.Enqueue($"[console.{m.Type}] {m.Text}");
        page.PageError += (_, e) => Logs.Enqueue($"[pageerror] {e}");
        await page.GotoAsync(url);
    }

    /// <summary>Die gesammelten Browser-Logs als ein String (für Assertion-Meldungen).</summary>
    public string DumpLogs() => string.Join("\n  ", Logs);

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) { try { await _browser.DisposeAsync(); } catch { /* best effort */ } }
        _playwright?.Dispose();
    }
}
