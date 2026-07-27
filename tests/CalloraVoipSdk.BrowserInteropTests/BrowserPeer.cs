using Microsoft.Playwright;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Ein echter headless-Browser-Peer (Playwright), der die von der Signaling-Bridge servierte
/// <c>peer.html</c> lädt und als WebRTC-Answerer gegen die SDK-Fassade connectet. Der konkrete Motor
/// (Chromium, Firefox, WebKit) kommt als <see cref="BrowserEngine"/> herein und liefert die
/// synthetische Media (fake-device) sowie das deaktivierte mDNS (echte host-IP-Candidates).
/// </summary>
public sealed class BrowserPeer(BrowserEngine engine) : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>Gesammelte Browser-Konsolen-/Fehler-Meldungen (Diagnose bei Fehlschlag).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Logs { get; } = new();

    public async Task NavigateAsync(string url)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await engine.LaunchAsync(_playwright);
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
