using Microsoft.AspNetCore.DataProtection;
namespace JobChatBot.Api;

public interface IAutoApplySteps
{
    Task<DiscoveryResult> Search(CancellationToken ct);
    Match[] Rank();
    Task<ApplicationAttempt> Open(string jobId);
    Task<ApplicationAttempt> Prepare(string attemptId);
    Task<ApplicationAttempt> Submit(string attemptId, string token);
    Task Close(string attemptId);
}
public sealed class LiveAutoApplySteps(WorkspaceStore store, JobDiscovery discovery, JobAgent agent, AssistedApplyService apply) : IAutoApplySteps
{
    public Task<DiscoveryResult> Search(CancellationToken ct) => discovery.Search(store, ct);
    public Match[] Rank() => store.Read(agent.Rank);
    public Task<ApplicationAttempt> Open(string jobId) => apply.Open(jobId);
    public Task<ApplicationAttempt> Prepare(string id) => apply.Continue(id);
    public Task<ApplicationAttempt> Submit(string id, string token) => apply.Submit(id, new(true, token));
    public async Task Close(string id) => await apply.Close(id);
}

public static class AutoApplyRun
{
    public static async Task Run(WorkspaceStore store, IAutoApplySteps steps, CancellationToken ct)
    {
        var initial = store.Read(d => d.Bot);
        if (!initial.Enabled || initial.Policy is null || !initial.Policy.AuthorizeSubmission) return;
        var started = DateTimeOffset.UtcNow;
        store.Update(d => { d.Bot = d.Bot with { Status = "searching", LastRunAt = started, SubmittedThisRun = 0 }; return true; });
        bool StillAuthorized() => store.Read(d => d.Bot.Enabled && d.Bot.AuthorizationId == initial.AuthorizationId && d.Bot.Policy?.AuthorizeSubmission == true && d.Bot.ProfileHash == AssistedApplyService.ProfileHash(d.Profile));
        void Pause(string message) => store.Update(d => { if (d.Bot.Enabled && d.Bot.AuthorizationId == initial.AuthorizationId) d.Bot = d.Bot with { Enabled = false, Status = "needs-user-action", Detail = message, NextRunAt = null }; return true; });
        if (!StillAuthorized()) { Pause("The saved resume/profile changed or authorization was stopped. Review it and start the bot again."); return; }
        try
        {
            async Task<bool> Finish(ApplicationAttempt attempt)
            {
                attempt = await steps.Prepare(attempt.Id);
                if (attempt.Status != "ready-to-submit" || attempt.ReviewToken is null) { Pause(attempt.Detail); return false; }
                if (!StillAuthorized()) { Pause("Authorization changed before Submit. Nothing further was clicked."); return false; }
                attempt = await steps.Submit(attempt.Id, attempt.ReviewToken);
                if (attempt.Status != "submitted") { Pause(attempt.Detail); return false; }
                store.Update(d => { if (d.Bot.AuthorizationId == initial.AuthorizationId) d.Bot = d.Bot with { SubmittedThisRun = d.Bot.SubmittedThisRun + 1 }; return true; });
                await steps.Close(attempt.Id);
                return true;
            }
            var pending = store.Read(d => d.ApplicationAttempts.FirstOrDefault(a => a.Status is "opening" or "needs-user-action" or "ready-to-submit" or "submitting" or "submission-unknown"));
            if (pending is not null)
            {
                if (pending.Status is "submitting" or "submission-unknown") { Pause("An earlier submission is uncertain. Check the site and Verify or record your own confirmation. It will not be retried automatically."); return; }
                var pendingMatch = steps.Rank().FirstOrDefault(m => m.Job.Id == pending.JobId);
                if (pendingMatch is null || pendingMatch.Score < initial.Policy.MinimumMatchScore || pendingMatch.Job.Url.Contains("naukri.com") && !initial.Policy.AllowNaukriProfileApply)
                { Pause("The unfinished application does not meet the current policy. Close that attempt before starting another."); return; }
                if (!await Finish(pending)) return;
            }
            var search = await steps.Search(ct);
            if (search.Status != "complete") { Pause(search.Message); return; }
            var plan = search.Plan;
            // Ranking measures keyword overlap only. A role keyword must also occur in the title.
            var roleWords = System.Text.RegularExpressions.Regex.Split(plan.Role.ToLowerInvariant(), @"[^a-z0-9+#.]+")
                .Where(w => w.Length > 1 && w is not "senior" and not "junior" and not "jobs").ToArray();
            var specific = roleWords.Where(w => w is not "developer" and not "engineer" and not "consultant" and not "specialist").ToArray();
            if (specific.Length > 0) roleWords = specific;
            var candidates = steps.Rank().Where(m => m.Score >= initial.Policy.MinimumMatchScore && AssistedApplyService.SupportedUrl(m.Job.Url)
                && roleWords.Any(w => m.Job.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Where(m => string.IsNullOrWhiteSpace(plan.Location) || (m.Job.Location + " " + m.Job.Title + " " + m.Job.Description).Contains(plan.Location, StringComparison.OrdinalIgnoreCase))
                .Where(m => !m.Job.Url.Contains("naukri.com", StringComparison.OrdinalIgnoreCase) || initial.Policy.AllowNaukriProfileApply)
                .Where(m => !store.Read(d => d.Applications.Any(a => a.JobId == m.Job.Id) || d.ApplicationAttempts.Any(a => a.JobId == m.Job.Id)))
                .Take(Math.Max(0, initial.Policy.MaxApplicationsPerRun - store.Read(d => d.Bot.SubmittedThisRun))).ToArray();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!StillAuthorized()) { Pause("Bot stopped or saved profile changed. No further applications will be started."); return; }
                store.Update(d => { d.Bot = d.Bot with { Status = "applying", Detail = "Preparing " + candidate.Job.Title + ". Only known fields are filled; unknown questions pause the bot." }; return true; });
                var attempt = await steps.Open(candidate.Job.Id);
                if (attempt.Status == "browser-unavailable") { Pause(attempt.Detail); return; }
                if (!await Finish(attempt)) return;
            }
            store.Update(d =>
            {
                if (d.Bot.Enabled && d.Bot.AuthorizationId == initial.AuthorizationId) d.Bot = d.Bot with { Status = "waiting", Detail = $"Run finished: {d.Bot.SubmittedThisRun} browser-confirmed applications. Other listings were skipped or no eligible jobs were found.", NextRunAt = DateTimeOffset.UtcNow.AddMinutes(initial.Policy.IntervalMinutes) };
                return true;
            });
        }
        catch (OperationCanceledException) { Pause("Backend stopped during a run. Check the attempt status before restarting. Uncertain submissions are not retried."); }
        catch (Exception) { Pause("Automation stopped after a browser/provider error. Check application attempts and the site before restarting. No success is assumed."); }
    }
}

public sealed class AutoApplyBot(WorkspaceCatalog catalog, IDataProtectionProvider protection, IConfiguration config, IHttpClientFactory clients, IApplyBrowser browser, ILogger<AutoApplyBot> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var store in catalog.All())
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var state = store.Read(d => d.Bot);
                    if (!state.Enabled || state.NextRunAt > DateTimeOffset.UtcNow) continue;
                    // An interrupted run is paused on restart instead of re-submitting in-flight forms.
                    if (state.Status is "searching" or "applying")
                    {
                        store.Update(d => { d.Bot = d.Bot with { Enabled = false, Status = "needs-user-action", Detail = "Backend restarted during a run. Check site and application attempts before starting again.", NextRunAt = null }; return true; });
                        continue;
                    }
                    var settings = new ProviderSettings(store, protection, config);
                    var agent = new JobAgent(clients, settings);
                    var discovery = new JobDiscovery(agent, settings, clients);
                    var service = new AssistedApplyService(store, browser, settings);
                    await AutoApplyRun.Run(store, new LiveAutoApplySteps(store, discovery, agent, service), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Bot workspace processing stopped ({Type}).", ex.GetType().Name); }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
