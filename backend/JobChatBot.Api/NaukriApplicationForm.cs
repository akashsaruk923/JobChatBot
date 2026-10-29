using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
namespace JobChatBot.Api;

public static class NaukriApplicationForm
{
    private static ILocator Apply(IPage page) => page.Locator("#apply-button").Filter(new() { HasTextRegex = new Regex("^(Apply|Apply now)$", RegexOptions.IgnoreCase) });
    private static string Token(IPage page, ResumeProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page.Url + AssistedApplyService.ProfileHash(profile) + "native-naukri-profile")));
    public static async Task<BrowserFormResult> Prepare(IPage page, ResumeProfile profile)
    {
        if (await BrowserApplicationForm.NeedsLogin(page)) return new("needs-user-action", "Login, OTP or CAPTCHA must be completed in Edge. No verification is bypassed.");
        var apply = Apply(page);
        if (await apply.CountAsync() != 1 || !await apply.IsVisibleAsync() || !await apply.IsEnabledAsync())
            return new("needs-user-action", "No supported Naukri native Apply button is visible. External sites and custom application questions require manual completion.");
        return new("ready-to-submit", "Naukri native Apply uses the resume and profile saved IN YOUR NAUKRI ACCOUNT, not JobChat's uploaded file. Enable Naukri profile apply only if you have checked that account's resume and answers.", Token(page, profile), [new("Application method", "Naukri saved profile/resume"), new("Listing", page.Url)]);
    }
    public static async Task<bool> SubmitReviewed(IPage page, ResumeProfile profile, string token)
    {
        var ready = await Prepare(page, profile);
        if (ready.Status != "ready-to-submit" || ready.ReviewToken != token) return false;
        await Apply(page).ClickAsync(new() { Timeout = 5000 });
        return true;
    }
    public static async Task<bool> HasReceipt(IPage page)
    {
        var success = page.Locator(".apply-message,.applied-message,.success-msg").Filter(new() { HasTextRegex = new Regex("(successfully applied|application (has been )?submitted)", RegexOptions.IgnoreCase) });
        return await success.CountAsync() == 1 && await success.IsVisibleAsync();
    }
}
