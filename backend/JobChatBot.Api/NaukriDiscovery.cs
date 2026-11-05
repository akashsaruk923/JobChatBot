using Microsoft.Playwright;
namespace JobChatBot.Api;
public record NaukriCard(string Title, string Company, string Location, string Url, string Description, string Experience);
public static class NaukriDiscovery
{
    public static async Task<DiscoveryResult> Search(IApplyBrowser browser, ProviderSettings settings, WorkspaceStore store, SearchPlan plan, CancellationToken ct)
    {
        var profile = store.Read(d => d.Profile);
        if (!profile.ExperienceYears.HasValue) return new("needs-profile", plan, 0, "Save your total years of experience first, for example 4.");
        if (!browser.Enabled) return new("browser-unavailable", plan, 0, "Naukri search requires this backend on your Windows machine with installed Edge. The Naukri website opens visibly; no API key is needed.");
        var gate = browser.Gate(store.DirectoryPath);
        await gate.WaitAsync(ct);
        try
        {
            if (store.Read(d => d.ApplicationAttempts.Any(a => a.Status is "opening" or "needs-user-action" or "ready-to-submit" or "submitting" or "submission-unknown")))
                return new("needs-user-action", plan, 0, "Finish or close your active application before starting another search. Its browser was preserved.");
            var url = plan.NaukriUrl + "?experience=" + profile.ExperienceYears.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var page = browser.Page(store.DirectoryPath, "naukri-search");
            if (page is null) page = await browser.Open(store.DirectoryPath, "naukri-search", url, settings["Naukri:Email"] ?? profile.Email);
            else await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            try { await page.Locator(".srp-jobtuple-wrapper, article.jobTuple").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 }); }
            catch (System.TimeoutException) { return new("needs-user-action", plan, 0, "Naukri has not shown supported job cards. Check the visible Edge window, complete login/OTP/CAPTCHA if requested, then Search again. No verification is bypassed."); }
            if (await BrowserApplicationForm.NeedsLogin(page)) return new("needs-user-action", plan, 0, "Complete Naukri login/verification in the visible Edge window, then search again.");
            var cards = await ReadCards(page);
            var jobs = Parse(cards, profile.ExperienceYears.Value);
            var added = store.Update(d => { var count = 0; foreach (var job in jobs) if (!d.Jobs.Any(j => j.Url == job.Url)) { d.Jobs.Add(job); count++; } return count; });
            return new("complete", plan, added, $"Read {cards.Length} visible Naukri cards; {jobs.Length} match your experience; added {added}. Listings with missing experience ranges were excluded. Apply uses your Naukri account's saved resume/profile in the visible Edge window.");
        }
        catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { return new("needs-user-action", plan, 0, "Naukri browser search could not finish. Check Edge, complete any verification and retry. No jobs or successful applications were invented."); }
        finally { gate.Release(); }
    }
    public static async Task<NaukriCard[]> ReadCards(IPage page)
    {
        var json = await page.Locator(".srp-jobtuple-wrapper, article.jobTuple").EvaluateAllAsync<string>("""
        cards => JSON.stringify(cards.slice(0, 25).map(c => {
          const text = s => c.querySelector(s)?.innerText?.trim() || '';
          const link = c.querySelector('a.title');
          return { Title: text('a.title'), Company: text('.comp-name, .subTitle'), Location: text('.locWdth, .location'), Url: link?.href || '', Description: c.innerText || '', Experience: text('.expwdth, .experience') };
        }))
        """);
        return System.Text.Json.JsonSerializer.Deserialize<NaukriCard[]>(json) ?? [];
    }
    public static Job[] Parse(NaukriCard[] cards, double years) => cards.Select(c => (Card:c, Range:Experience.Range(c.Experience)))
        .Where(x => x.Range.HasValue && Experience.Fits(years, x.Range.Value.Min, x.Range.Value.Max) && x.Card.Title.Length > 0
            && AssistedApplyService.SupportedUrl(x.Card.Url) && new Uri(x.Card.Url).Host is "www.naukri.com" or "naukri.com")
        .Select(x => new Job(Guid.NewGuid().ToString("N"), x.Card.Title, x.Card.Company, x.Card.Location, new Uri(x.Card.Url).GetLeftPart(UriPartial.Path), x.Card.Description, DateTimeOffset.UtcNow, "Naukri", "visible-listing-card", x.Range!.Value.Min, x.Range.Value.Max))
        .DistinctBy(j => j.Url).ToArray();
}
