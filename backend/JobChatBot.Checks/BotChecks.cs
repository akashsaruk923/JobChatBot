using JobChatBot.Api;
namespace JobChatBot.Checks;

internal static class BotChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var store = new WorkspaceStore(Path.Combine(root, "bot-test"));
        var profile = new ResumeProfile(Name: "Test Person", Email: "test@example.com", ResumeText: ".NET SQL", DesiredRole: ".NET Developer");
        var policy = new BotPolicy(true, 70, 2, 60);
        void Reset() => store.Update(d =>
        {
            d.Profile = profile; d.Applications.Clear(); d.ApplicationAttempts.Clear();
            d.Bot = new(true, "queued", "", policy, AssistedApplyService.ProfileHash(profile), DateTimeOffset.UtcNow);
            d.Jobs = Enumerable.Range(1, 4).Select(i => new Job(i.ToString(), ".NET Developer", "Test", "", "https://www.linkedin.com/jobs/view/" + i, ".NET SQL", DateTimeOffset.UtcNow)).ToList(); return true;
        });
        Reset();
        var steps = new FakeSteps(store);
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 2 && store.Read(d => d.Bot.SubmittedThisRun) == 2, "Respect maximum applications per run");
        Check(store.Read(d => d.Bot.Enabled && d.Bot.Status == "waiting" && d.Bot.NextRunAt > DateTimeOffset.UtcNow), "Successful run schedules next interval");
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 4 && store.Read(d => d.Applications.Count) == 4, "Previously applied jobs must not be submitted twice");
        Reset(); steps = new FakeSteps(store) { UnknownQuestion = true };
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 0 && !store.Read(d => d.Bot.Enabled), "Unknown question pauses bot before submit");
        Reset(); steps = new FakeSteps(store) { UncertainSubmission = true };
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 1 && !store.Read(d => d.Bot.Enabled) && store.Read(d => d.Applications.Count) == 0, "Unknown confirmation pauses bot without inventing application success");
        Reset(); steps = new FakeSteps(store) { StopBeforeSubmit = true };
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 0, "Stop request before submission is honored");
        Reset(); store.Update(d => { d.Profile = d.Profile with { ResumeText = "Changed resume" }; return true; }); steps = new(store);
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 0 && !store.Read(d => d.Bot.Enabled), "Profile changes invalidate unattended authorization");
        Reset(); store.Update(d => { d.Jobs[0] = d.Jobs[0] with { Url = "https://www.naukri.com/job-listings-test" }; return true; }); steps = new(store);
        await AutoApplyRun.Run(store, steps, default);
        Check(!steps.Opened.Contains("1"), "Naukri saved-profile apply requires separate opt-in");
        Reset(); steps = new FakeSteps(store) { ReplaceAuthorization = true };
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 0 && store.Read(d => d.Bot.AuthorizationId) == "new-authorization" && store.Read(d => d.Bot.Enabled), "Old run cannot inherit or overwrite new authorization after stop/start");
        Reset(); store.Update(d => { d.ApplicationAttempts.Add(new("pending", "1", "submission-unknown", "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)); return true; }); steps = new(store);
        await AutoApplyRun.Run(store, steps, default);
        Check(steps.Submits == 0 && steps.Searches == 0 && !store.Read(d => d.Bot.Enabled), "Uncertain pending submission must pause before another search or submit");
        return count;
    }
    private sealed class FakeSteps(WorkspaceStore store) : IAutoApplySteps
    {
        public int Submits, Searches; public bool UnknownQuestion, UncertainSubmission, StopBeforeSubmit, ReplaceAuthorization;
        public List<string> Opened { get; } = [];
        public Task<DiscoveryResult> Search(CancellationToken ct) { Searches++; return Task.FromResult(new DiscoveryResult("complete", new(".NET Developer", "", [".NET"], "", "", ""), 0, "")); }
        public Match[] Rank() => store.Read(d => d.Jobs.Select(j => new Match(j, 100, [".NET"], [])).ToArray());
        public Task<ApplicationAttempt> Open(string id) { Opened.Add(id); return Task.FromResult(Attempt(id, "needs-user-action")); }
        public Task<ApplicationAttempt> Prepare(string id) { if (ReplaceAuthorization) store.Update(d => { d.Bot = d.Bot with { AuthorizationId = "new-authorization", Enabled = true, Status = "queued" }; return true; }); if (StopBeforeSubmit) store.Update(d => { d.Bot = d.Bot with { Enabled = false }; return true; }); return Task.FromResult(Attempt(id, UnknownQuestion ? "needs-user-action" : "ready-to-submit") with { ReviewToken = "test-token" }); }
        public Task<ApplicationAttempt> Submit(string id, string token) { Submits++; if (!UncertainSubmission) store.Update(d => { d.Applications.Add(new(id, id, "browser-confirmed", DateTimeOffset.UtcNow)); return true; }); return Task.FromResult(Attempt(id, UncertainSubmission ? "submission-unknown" : "submitted")); }
        public Task Close(string id) => Task.CompletedTask;
        private static ApplicationAttempt Attempt(string id, string status) => new(id, id, status, "Fixture result", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
}
