using JobChatBot.Api;
using Microsoft.Playwright;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

internal static class BrowserChecks
{
    public static async Task<int> Run()
    {
        var executable = Environment.GetEnvironmentVariable("BROWSER_TEST_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Set BROWSER_TEST_EXECUTABLE to an installed Chromium/Edge executable for offline browser checks.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { ExecutablePath = executable, Headless = true, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(3000);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        const string form = """
        <div role="dialog"><label for="first">First name</label><input id="first" required><label for="last">Last name</label><input id="last" required><label for="email">Email address</label><input id="email" type="email" required><button id="submit" onclick="window.submits=(window.submits||0)+1;this.parentElement.innerHTML='<h2>Application sent</h2>'">Submit application</button></div>
        """;
        await page.SetContentAsync(form);
        var profile = new ResumeProfile(Name: "Test Person", Email: "test@example.com", ResumeText: ".NET", FirstName: "Test", LastName: "Person");
        var ready = await BrowserApplicationForm.Prepare(page, profile, null);
        Check(ready.Status == "ready-to-submit" && ready.ReviewToken is not null, "Known fields should produce a review token, not submit");
        Check(await page.Locator("#first").InputValueAsync() == "Test", "Only supplied first name should be filled");
        Check(await page.EvaluateAsync<int>("window.submits||0") == 0, "Preparation must not submit");
        await page.Locator("#email").FillAsync("changed@example.com");
        Check(!await BrowserApplicationForm.SubmitReviewed(page, ready.ReviewToken!), "Changed form invalidates approval token");
        ready = await BrowserApplicationForm.Prepare(page, profile, null);
        Check(await BrowserApplicationForm.SubmitReviewed(page, ready.ReviewToken!), "Current reviewed form can be submitted");
        Check(await page.EvaluateAsync<int>("window.submits") == 1 && await BrowserApplicationForm.HasReceipt(page), "Require recognized modal receipt after exactly one click");
        await page.SetContentAsync("<p>Application sent</p>");
        Check(!await BrowserApplicationForm.HasReceipt(page), "Incidental job description text is not a receipt");
        await page.SetContentAsync("""<div role="dialog"><label for="years">Years of experience</label><input id="years" required><button>Submit application</button></div>""");
        var missing = await BrowserApplicationForm.Prepare(page, profile, null);
        Check(missing.Status == "needs-user-action" && await page.Locator("#years").InputValueAsync() == "", "Experience answers must never be guessed");
        await page.SetContentAsync("<input type=password>");
        Check((await BrowserApplicationForm.Prepare(page, profile, null)).Status == "needs-user-action", "Login must pause form automation");
        await page.SetContentAsync("<div role=dialog><label for=country>Phone country code</label><select id=country><option>USA (+1)</option><option>India (+91)</option></select><label for=phone>Mobile phone number</label><input id=phone required><button>Submit application</button></div>");
        var phoneProfile = profile with { PhoneNumber = "+919876543210" };
        Check((await BrowserApplicationForm.Prepare(page, phoneProfile, null)).Status == "needs-user-action", "Mismatched phone country must pause before applying");
        await page.Locator("#country").SelectOptionAsync(new SelectOptionValue { Label = "India (+91)" });
        Check((await BrowserApplicationForm.Prepare(page, phoneProfile, null)).Status == "ready-to-submit" && await page.Locator("#phone").InputValueAsync() == "9876543210", "Keep the supplied phone consistent with the site's separate country code");
        var uploadPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllTextAsync(uploadPath, "offline fixture content");
            await page.SetContentAsync("<div role=dialog><label for=resume>Resume</label><input id=resume type=file required accept=.pdf style='display:none'><button>Submit application</button></div>");
            Check((await BrowserApplicationForm.Prepare(page, profile, null)).Status == "needs-user-action", "Hidden required resume input must not be skipped");
            Check((await BrowserApplicationForm.Prepare(page, profile, uploadPath, "MyResume.pdf")).Status == "ready-to-submit", "Matching resume attachment should allow review");
            Check(await page.Locator("#resume").EvaluateAsync<string>("e=>e.files[0].name") == "MyResume.pdf", "Original resume filename must be preserved");
        }
        finally { File.Delete(uploadPath); }
        await page.SetContentAsync("<button id='apply-button' onclick=\"window.native=(window.native||0)+1;this.outerHTML='<div class=success-msg>You have successfully applied</div>'\">Apply</button>");
        var native = await NaukriApplicationForm.Prepare(page, profile);
        Check(native.Status == "ready-to-submit" && native.Detail.Contains("NAUKRI ACCOUNT"), "Naukri must explicitly identify its saved-profile source");
        Check(await page.EvaluateAsync<int>("window.native||0") == 0, "Naukri preparation must not click Apply");
        Check(await NaukriApplicationForm.SubmitReviewed(page, profile, native.ReviewToken!), "Naukri native Apply requires reviewed token");
        Check(await NaukriApplicationForm.HasReceipt(page), "Recognized native confirmation required");
        await page.SetContentAsync("<div class=success-msg>Something went wrong</div>");
        Check(!await NaukriApplicationForm.HasReceipt(page), "Error state must not be recorded as applied");
        Check(!AssistedApplyService.SupportedUrl("https://linkedin.com.evil.example/jobs/view/1"), "Reject lookalike origins");
        Check(!AssistedApplyService.SupportedUrl("https://www.linkedin.com:9443/jobs/view/1"), "Reject unexpected browser ports");
        Check(AssistedApplyService.SupportedUrl("https://www.linkedin.com/jobs/view/123"), "Allow supported original listing");
        var root = Path.Combine(Path.GetTempPath(), "jobchat-browser-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(root);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var settings = new ProviderSettings(store, protection, new ConfigurationBuilder().Build());
            const string url = "https://www.linkedin.com/jobs/view/test-fixture";
            await page.RouteAsync("**/*", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = form }));
            var runtime = new FixtureBrowser(page);
            var service = new AssistedApplyService(store, runtime, settings);
            store.Update(d => { d.Profile = profile; d.Jobs.Add(new("fixture-job", ".NET Developer", "Test", "", url, ".NET", DateTimeOffset.UtcNow)); return true; });
            var attempt = await service.Open("fixture-job");
            attempt = await service.Continue(attempt.Id);
            Check(attempt.Status == "ready-to-submit", "Coordinator prepares without final click");
            var token = attempt.ReviewToken!;
            settings.Save(new(new() { ["LinkedIn:Email"] = "different-account@example.com" }));
            try { await service.Submit(attempt.Id, new(true, token)); Check(false, "Changed login identity must invalidate review"); }
            catch (ApplyFlowException ex) { Check(ex.StatusCode == 409, "Reject submission after login identity change"); }
            settings.Clear();
            await Task.WhenAll(service.Submit(attempt.Id, new(true, token)), service.Submit(attempt.Id, new(true, token)));
            Check(await page.EvaluateAsync<int>("window.submits") == 1, "Concurrent submission requests must click once");
            Check(store.Read(d => d.Applications.Count) == 1 && store.Read(d => d.Notifications.Count) == 2, "Confirmation queues notifications exactly once");
            store.Update(d => { d.Applications.Clear(); d.Notifications.Clear(); d.ApplicationAttempts.Clear(); return true; });
            await page.UnrouteAllAsync();
            await page.RouteAsync("**/*", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = form.Replace("<h2>Application sent</h2>", "<p>Pending status</p>") }));
            attempt = await service.Open("fixture-job"); attempt = await service.Continue(attempt.Id); token = attempt.ReviewToken!;
            var uncertain = await service.Submit(attempt.Id, new(true, token));
            Check(uncertain.Status == "submission-unknown" && store.Read(d => d.Applications.Count) == 0 && store.Read(d => d.Notifications.Count) == 0, "Unknown receipt must create neither application nor notifications");
            try { await service.Submit(attempt.Id, new(true, token)); Check(false, "Uncertain request must not be retried"); }
            catch (ApplyFlowException ex) { Check(ex.StatusCode == 409, "Uncertain submission is durably blocked from retry"); }
            Check(await page.EvaluateAsync<int>("window.submits") == 1, "No duplicate click after unknown outcome");
            try { await service.ResolveNotSubmitted(attempt.Id, new(false)); Check(false, "Explicit site check required"); }
            catch (ApplyFlowException ex) { Check(ex.StatusCode == 400, "Unknown outcome cannot be cleared without a site check"); }
            Check((await service.ResolveNotSubmitted(attempt.Id, new(true))).Status == "closed" && store.Read(d => d.Notifications.Count) == 0, "Resolving not-submitted creates no success or messages");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return checks;
    }
}

internal sealed class FixtureBrowser(IPage page) : IApplyBrowser
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Enabled => true;
    public SemaphoreSlim Gate(string workspace) => gate;
    public IPage? Page(string workspace, string attemptId) => page;
    public async Task<IPage> Open(string workspace, string attemptId, string url, string identity) { await page.GotoAsync(url); return page; }
    public Task Close(string workspace) => Task.CompletedTask;
}
