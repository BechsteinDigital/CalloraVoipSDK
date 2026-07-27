using Microsoft.Playwright;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class PlaywrightLaunchSmokeTests
{
    [ChromiumFact] public Task Chromium_Launches() => LaunchesHeadless(BrowserEngine.Chromium);
    [FirefoxFact]  public Task Firefox_Launches()  => LaunchesHeadless(BrowserEngine.Firefox);
    [WebKitFact]   public Task WebKit_Launches()   => LaunchesHeadless(BrowserEngine.WebKit);

    private static async Task LaunchesHeadless(BrowserEngine engine)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await engine.LaunchAsync(playwright);
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<html><body><h1 id='t'>ok</h1></body></html>");
        var text = await page.InnerTextAsync("#t");
        Assert.Equal("ok", text);
    }
}
