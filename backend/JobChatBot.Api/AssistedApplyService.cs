using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
namespace JobChatBot.Api;

public sealed class AssistedApplyService(WorkspaceStore store, IApplyBrowser browser, ProviderSettings settings)
{
    private static readonly HashSet<string> Terminal = ["submitted", "user-confirmed", "closed", "browser-unavailable"];
    public static string ProfileHash(ResumeProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile))));
    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public async Task<ApplicationAttempt> Open(string jobId)
    {
        var gate = browser.Gate(store.DirectoryPath); await gate.WaitAsync();
        try
        {
            var data = store.Read(d => d);
            var job = data.Jobs.FirstOrDefault(j => j.Id == jobId) ?? throw new ApplyFlowException(404, "Job not found.");
            if (!SupportedUrl(job.Url)) throw new ApplyFlowException(409, "Only direct HTTPS LinkedIn/Naukri listing URLs can be opened by assisted apply. Use the original website for other employers.");
            if (data.Applications.Any(a => a.JobId == jobId)) throw new ApplyFlowException(409, "An application is already recorded for this job. No duplicate submission will be started.");
            if (string.IsNullOrWhiteSpace(data.Profile.Name) || !MailAddress.TryCreate(data.Profile.Email, out _) || string.IsNullOrWhiteSpace(data.Profile.ResumeText))
                throw new ApplyFlowException(400, "Save your full name, valid email and resume before starting assisted apply.");
            var existing = data.ApplicationAttempts.LastOrDefault(a => a.JobId == jobId && !Terminal.Contains(a.Status));
            if (existing is not null) return existing;
            if (data.ApplicationAttempts.Any(a => !Terminal.Contains(a.Status))) throw new ApplyFlowException(409, "Finish or close your active application browser before opening another job.");
            if (!browser.Enabled) throw new ApplyFlowException(409, "Run the backend locally on Windows with installed Microsoft Edge for assisted apply. No official submission API is connected.");
            var now = DateTimeOffset.UtcNow;
            var attempt = new ApplicationAttempt(Guid.NewGuid().ToString("N"), jobId, "opening", "Opening a separate local Edge profile. Sign in yourself; no account password is stored by the application.", ProfileHash(data.Profile), now, now, LoginIdentityHash: IdentityHash(job));
            store.Update(d => { d.ApplicationAttempts.Add(attempt); return true; });
            try
            {
                var page = await browser.Open(store.DirectoryPath, attempt.Id, job.Url, settings[(new Uri(job.Url).Host.Contains("linkedin") ? "LinkedIn" : "Naukri") + ":Email"] ?? data.Profile.Email);
                await BrowserLogin.TryLogin(page, settings, job.Url);
                return Set(attempt.Id, "needs-user-action", "Sign in to the job site in Edge if needed. For LinkedIn Easy Apply, click Continue in JobChat to fill known fields. Naukri forms require manual completion in Edge.");
            }
            catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException)
            {
                await browser.Close(store.DirectoryPath);
                return Set(attempt.Id, "browser-unavailable", "Could not start installed Edge or open the listing. Check Edge installation and connectivity. No application was submitted.");
            }
        }
        finally { gate.Release(); }
    }
    public Task<ApplicationAttempt> Continue(string id) => WithPage(id, async (attempt, job, profile, page) =>
    {
        if (attempt.Status is "submitting" or "submission-unknown") return await VerifyReceipt(attempt, job, page);
        if (attempt.LoginIdentityHash != IdentityHash(job)) throw new ApplyFlowException(409, "The job-site login identity changed. Close this attempt and open a new one for the correct account.");
        if (attempt.ProfileHash != ProfileHash(profile)) throw new ApplyFlowException(409, "Your profile changed. Close this attempt and start a new one so the correct resume is reviewed.");
        if (!SameListing(page.Url, job.Url)) return Set(id, "needs-user-action", "Return to this job's original listing after signing in. External employer pages require manual application.");
        if (new Uri(job.Url).Host.EndsWith("naukri.com", StringComparison.OrdinalIgnoreCase))
        {
            var native = await NaukriApplicationForm.Prepare(page, profile);
            return Set(id, native.Status, native.Detail, native.ReviewToken, native.Fields);
        }
        var asset = store.Read(d => d.ResumeAsset);
        var path = asset is not null && asset.TextHash == TextHash(profile.ResumeText) && Path.GetFileName(asset.StoredName) == asset.StoredName
            ? Path.Combine(store.DirectoryPath, asset.StoredName) : null;
        if (path is not null && !File.Exists(path)) path = null;
        var result = await BrowserApplicationForm.Prepare(page, profile, path, asset?.OriginalName);
        return Set(id, result.Status, result.Detail, result.ReviewToken, result.Fields);
    });
    public Task<ApplicationAttempt> Submit(string id, SubmitApplicationInput input)
    {
        if (!input.Approved || string.IsNullOrWhiteSpace(input.ReviewToken)) throw new ApplyFlowException(400, "Explicit approval and the current review token are required. Nothing was submitted.");
        return WithPage(id, async (attempt, job, profile, page) =>
        {
            if (attempt.Status != "ready-to-submit" || attempt.ReviewToken != input.ReviewToken || attempt.ProfileHash != ProfileHash(profile) || attempt.LoginIdentityHash != IdentityHash(job))
                throw new ApplyFlowException(409, "Review is missing, stale or already used. Continue the form and review it again. Uncertain submissions are never retried automatically.");
            if (!SameListing(page.Url, job.Url)) throw new ApplyFlowException(409, "The browser is no longer on the reviewed job listing.");
            // Durable transition BEFORE the final click: retries/restarts can never click Submit again.
            Set(id, "submitting", "Submission requested once. Waiting for a recognized site confirmation.");
            bool clicked;
            try { clicked = new Uri(job.Url).Host.Contains("naukri") ? await NaukriApplicationForm.SubmitReviewed(page, profile, input.ReviewToken) : await BrowserApplicationForm.SubmitReviewed(page, input.ReviewToken); }
            catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { return Set(id, "submission-unknown", "The browser operation was interrupted. Check the original site; no retry or notification is performed automatically."); }
            if (!clicked) return Set(id, "needs-user-action", "The form or answers changed. Review them in Edge and click Continue again. No final submit button was clicked.");
            for (var i = 0; i < 10; i++)
            {
                if (await HasReceipt(job, page)) return RecordVerified(id, job);
                await Task.Delay(500);
            }
            return Set(id, "submission-unknown", "Submit was clicked, but no supported confirmation was detected. Check Edge and use Verify. Do not retry the application.");
        });
    }
    public Task<ApplicationAttempt> Verify(string id) => WithPage(id, (attempt, job, _, page) => VerifyReceipt(attempt, job, page));
    public async Task<ApplicationAttempt> ResolveNotSubmitted(string id, ResolveUnknownInput input)
    {
        if (!input.CheckedOriginalSite) throw new ApplyFlowException(400, "Check the original site's application history first. Unknown submissions must never be guessed or retried automatically.");
        var gate = browser.Gate(store.DirectoryPath); await gate.WaitAsync();
        try
        {
            var attempt = Get(id);
            if (attempt.Status != "submission-unknown") throw new ApplyFlowException(409, "Only a completed uncertain attempt can be resolved. An in-flight Submit cannot be cleared.");
            await browser.Close(store.DirectoryPath);
            return Set(id, "closed", "The person checked the original site and reported that no application was submitted. No success or notifications were created. This attempted job remains excluded from automatic retries.");
        }
        finally { gate.Release(); }
    }
    public async Task<ApplicationAttempt> Close(string id)
    {
        var gate = browser.Gate(store.DirectoryPath); await gate.WaitAsync();
        try
        {
            var attempt = Get(id);
            await browser.Close(store.DirectoryPath);
            return attempt.Status is "submitted" or "submission-unknown" or "submitting" ? attempt : Set(id, "closed", "Application browser closed. No submission was recorded.");
        }
        finally { gate.Release(); }
    }
    private async Task<ApplicationAttempt> WithPage(string id, Func<ApplicationAttempt, Job, ResumeProfile, IPage, Task<ApplicationAttempt>> action)
    {
        var gate = browser.Gate(store.DirectoryPath); await gate.WaitAsync();
        try
        {
            var attempt = Get(id);
            if (attempt.Status == "submitted") return attempt;
            var page = browser.Page(store.DirectoryPath, id);
            if (page is null) throw new ApplyFlowException(409, "This application's browser is closed or the backend restarted. Check the site manually. No retry is made for an uncertain submission.");
            var data = store.Read(d => d);
            var job = data.Jobs.First(j => j.Id == attempt.JobId);
            try { return await action(attempt, job, data.Profile, page); }
            catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { return Set(id, Get(id).Status is "submitting" or "submission-unknown" ? "submission-unknown" : "needs-user-action", "Browser changed or closed during the operation. Check the original site manually. No success or retry is assumed."); }
        }
        finally { gate.Release(); }
    }
    private async Task<ApplicationAttempt> VerifyReceipt(ApplicationAttempt attempt, Job job, IPage page)
    {
        if (SameListing(page.Url, job.Url) && await HasReceipt(job, page)) return RecordVerified(attempt.Id, job);
        return Set(attempt.Id, attempt.Status is "submitting" or "submission-unknown" ? "submission-unknown" : "needs-user-action", "No supported site confirmation is visible. Check Edge manually. No application or notification was created.");
    }
    private static Task<bool> HasReceipt(Job job, IPage page) => new Uri(job.Url).Host.Contains("naukri") ? NaukriApplicationForm.HasReceipt(page) : BrowserApplicationForm.HasReceipt(page);
    private ApplicationAttempt RecordVerified(string id, Job job) => store.Update(d =>
    {
        var index = d.ApplicationAttempts.FindIndex(a => a.Id == id);
        var attempt = d.ApplicationAttempts[index];
        if (!d.Applications.Any(a => a.JobId == job.Id))
        {
            var application = new Application(Guid.NewGuid().ToString("N"), job.Id, "browser-confirmed", DateTimeOffset.UtcNow);
            d.Applications.Add(application);
            foreach (var channel in new[] { "email", "whatsapp" }) d.Notifications.Add(new(Guid.NewGuid().ToString("N"), application.Id, channel, "pending", "A recognized confirmation was observed in the local job-site browser. Notification delivery is separate.", DateTimeOffset.UtcNow));
        }
        return d.ApplicationAttempts[index] = attempt with { Status = "submitted", Detail = "A supported application confirmation was observed in Edge. This is browser evidence, not a platform API receipt or hiring decision.", ReviewToken = null, Fields = null, UpdatedAt = DateTimeOffset.UtcNow };
    });
    private string IdentityHash(Job job) => TextHash((new Uri(job.Url).Host.Contains("linkedin") ? "LinkedIn:" + settings["LinkedIn:Email"] : "Naukri:" + settings["Naukri:Email"]) ?? "");
    private ApplicationAttempt Get(string id) => store.Read(d => d.ApplicationAttempts.FirstOrDefault(a => a.Id == id)) ?? throw new ApplyFlowException(404, "Application attempt not found in this workspace.");
    private ApplicationAttempt Set(string id, string status, string detail, string? token = null, ReviewField[]? fields = null) => store.Update(d =>
    {
        var index = d.ApplicationAttempts.FindIndex(a => a.Id == id);
        return d.ApplicationAttempts[index] = d.ApplicationAttempts[index] with { Status = status, Detail = detail, ReviewToken = token, Fields = fields, UpdatedAt = DateTimeOffset.UtcNow };
    });
    public static bool SupportedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 && u.IsDefaultPort
        && ((u.Host is "www.linkedin.com" or "linkedin.com" && u.AbsolutePath.StartsWith("/jobs/view/")) || (u.Host is "www.naukri.com" or "naukri.com" && u.AbsolutePath.StartsWith("/job-listings-")));
    private static bool SameListing(string current, string original) => SupportedUrl(current) && new Uri(current).Host.Replace("www.", "") == new Uri(original).Host.Replace("www.", "") && new Uri(current).AbsolutePath.TrimEnd('/') == new Uri(original).AbsolutePath.TrimEnd('/');
}
