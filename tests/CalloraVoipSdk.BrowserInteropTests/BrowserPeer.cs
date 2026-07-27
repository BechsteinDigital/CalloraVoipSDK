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
        await page.GotoAsync(url);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) { try { await _browser.DisposeAsync(); } catch { /* best effort */ } }
        _playwright?.Dispose();
    }
}
