using Microsoft.Playwright;
using Xunit;

namespace CalloraVoipSdk.BrowserInteropTests;

[Trait("Category", "BrowserInterop")]
public sealed class PlaywrightLaunchSmokeTests
{
    [BrowserRequiredFact]
    public async Task Chromium_Launches_Headless_And_Loads_Blank_Page()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = BrowserRequiredFactAttribute.ChromiumPath,
        });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<html><body><h1 id='t'>ok</h1></body></html>");
        var text = await page.InnerTextAsync("#t");
        Assert.Equal("ok", text);
    }
}
