using System.Text.RegularExpressions;
using Microsoft.Playwright;
namespace JobChatBot.Api;

public static class BrowserLogin
{
    public static async Task TryLogin(IPage page, ProviderSettings settings, string listing)
    {
        var source = new Uri(listing).Host.Contains("linkedin") ? "LinkedIn" : "Naukri";
        if (!settings.Has(source + ":Email", source + ":Password")) return;
        // Navigate only to fixed provider login origins. Never type a password into a job description or external employer form.
        var login = source == "LinkedIn" ? "https://www.linkedin.com/login" : "https://www.naukri.com/nlogin/login";
        await page.GotoAsync(login, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        var origin = new Uri(page.Url);
        if (origin.Scheme != "https" || origin.Host != "www." + source.ToLowerInvariant() + ".com") return;
        if (Regex.IsMatch(origin.AbsolutePath, "challenge|checkpoint", RegexOptions.IgnoreCase)) return;
        var email = source == "LinkedIn" ? page.Locator("#username") : page.Locator("#usernameField");
        var password = source == "LinkedIn" ? page.Locator("#password") : page.Locator("#passwordField");
        if (await email.CountAsync() == 1 && await password.CountAsync() == 1 && await email.IsVisibleAsync() && await password.IsVisibleAsync())
        {
            await email.FillAsync(settings[source + ":Email"]!);
            await password.FillAsync(settings[source + ":Password"]!);
            var submit = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(source == "LinkedIn" ? "^Sign in$" : "^Login$", RegexOptions.IgnoreCase) });
            if (await submit.CountAsync() == 1 && await submit.IsVisibleAsync())
            {
                await submit.ClickAsync(new() { Timeout = 5000 });
                try { await page.WaitForURLAsync(u => !u.Contains("/login") && !u.Contains("/nlogin"), new() { Timeout = 10000 }); }
                catch (System.TimeoutException) { return; }
            }
        }
        if (Regex.IsMatch(page.Url, "challenge|checkpoint|/login|/nlogin", RegexOptions.IgnoreCase)) return;
        await page.GotoAsync(listing, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
    }
}
