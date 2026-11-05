using JobChatBot.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

if (args.Contains("--help"))
{
    Console.WriteLine("NaukriTool: visible Edge, no API keys, manual login, bounded auto-apply, resume/name update every 30 minutes.\nRun scripts\\naukri-tool.cmd on Windows. Configuration/state: .NaukriTool in the repository.\nOptions: --data-dir <absolute path> (separate local account/state).\nControls: Ctrl+C stops; paused runs require RESUME; START authorizes each launch.\nNo live-site compatibility is claimed. Unsupported pages pause and write structural diagnostics.");
    return;
}
if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("NaukriTool must run on your Windows PC with installed Edge. No local Windows browser can be opened from Linux/cloud."); Environment.ExitCode=1; return; }
var root = Directory.GetCurrentDirectory();
var dataIndex = Array.IndexOf(args,"--data-dir");
var directory = dataIndex >= 0 && dataIndex+1 < args.Length ? Path.GetFullPath(args[dataIndex+1]) : Path.Combine(root,".NaukriTool");
Directory.CreateDirectory(directory);
// One instance per private store: never race profile uploads or final application clicks.
using var instance = new FileStream(Path.Combine(directory,"tool.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel=true; stop.Cancel(); };
async Task<string?> ReadCommand() => await Task.Run(Console.ReadLine).WaitAsync(stop.Token);
WorkspaceStore? store = null;
await using var browser = new ToolBrowser(directory);
try
{
    var config = ToolConfig.LoadOrCreate(Path.Combine(directory,"config.json"));
    var profile = config.Profile();
    store = new WorkspaceStore(Path.Combine(directory,"workspace"));
    store.Update(d => {
        d.Profile=profile; d.Bot=d.Bot with { Enabled=false, Status="stopped", NextRunAt=null };
        for (var i=0;i<d.ApplicationAttempts.Count;i++) if (d.ApplicationAttempts[i].Status == "submitting")
            d.ApplicationAttempts[i]=d.ApplicationAttempts[i] with { Status="submission-unknown",Detail="Previous process ended during submission; verify on Naukri before resolving." };
        return true;
    });
    var server = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Notifications:DisableSending"]="true" }).Build();
    var settings = new ProviderSettings(store,DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"keys"))),server);
    var clients = new ToolClients();
    var agent = new JobAgent(clients,settings);
    var discovery = new JobDiscovery(agent,settings,clients,browser);
    var apply = new AssistedApplyService(store,browser,settings);
    Console.WriteLine($"Name: {config.Name}\nResume: {Path.GetFileName(config.ResumePath)}\nTarget: {config.Role} | {config.Location} | {config.ExperienceYears} years\nEvery 30 minutes: upload this resume, verify/correct this name, search, and apply to at most {config.MaxApplicationsPerCycle} matching native Naukri jobs.\nApplications use your Naukri account's saved resume/answers. No unknown answers are invented. No API keys or stored passwords.");
    Console.WriteLine("Type START to authorize these real profile updates and bounded real applications; anything else exits.");
    if (await ReadCommand() != "START") return;
    await browser.Open(store.DirectoryPath,"login","https://www.naukri.com/mnjuser/profile",config.Email);
    Console.WriteLine("Sign in to the CORRECT account in Edge, check your Naukri resume/answers, then press Enter. Complete OTP/CAPTCHA yourself.");
    await ReadCommand();
    var policy = new BotPolicy(true,config.MinimumMatchScore,config.MaxApplicationsPerCycle,30,true);
    void Authorize() => store.Update(d => { d.Bot=new(true,"queued","",policy,AssistedApplyService.ProfileHash(d.Profile),DateTimeOffset.UtcNow,AuthorizationId:Guid.NewGuid().ToString("N")); return true; });
    Authorize();
    var nextProfileUpdate = DateTimeOffset.UtcNow;
    var nextApply = DateTimeOffset.UtcNow;
    while (!stop.IsCancellationRequested)
    {
        var pending = store.Read(d => d.ApplicationAttempts.FirstOrDefault(a => a.Status is "opening" or "needs-user-action" or "ready-to-submit" or "submitting" or "submission-unknown"));
        if (pending?.Status is "submitting" or "submission-unknown")
        {
            var job = store.Read(d => d.Jobs.First(j => j.Id == pending.JobId));
            Console.WriteLine($"Uncertain application: {job.Title} | {job.Url}. Check Naukri application history manually. It will never be automatically retried.");
            Console.WriteLine("Only after checking the site, type CONFIRMED-APPLIED or NOT-SUBMITTED. Anything else stops.");
            var resolution = await ReadCommand();
            if (resolution == "NOT-SUBMITTED") await apply.ResolveNotSubmitted(pending.Id,new(true));
            else if (resolution == "CONFIRMED-APPLIED") store.Update(d => {
                var index=d.ApplicationAttempts.FindIndex(a=>a.Id==pending.Id);
                d.ApplicationAttempts[index]=pending with { Status="user-confirmed",Detail="User checked Naukri history and confirmed submission. Not an automated site receipt.",ReviewToken=null,UpdatedAt=DateTimeOffset.UtcNow };
                if (!d.Applications.Any(a=>a.JobId==job.Id)) d.Applications.Add(new(Guid.NewGuid().ToString("N"),job.Id,"user-confirmed",DateTimeOffset.UtcNow));
                return true;
            });
            else break;
            Authorize(); nextApply=DateTimeOffset.UtcNow; continue;
        }
        if (pending is not null && browser.Page(store.DirectoryPath,pending.Id) is null)
        {
            var pendingJob = store.Read(d => d.Jobs.First(j => j.Id == pending.JobId));
            await browser.Open(store.DirectoryPath,pending.Id,pendingJob.Url,config.Email);
        }
        if (DateTimeOffset.UtcNow >= nextProfileUpdate && pending is null)
        {
            // Reject file changes mid-session: extracted matching data and reviewed upload must agree.
            if (AssistedApplyService.TextHash(ResumeReader.Extract(File.ReadAllBytes(config.ResumePath),config.ResumePath)) != AssistedApplyService.TextHash(profile.ResumeText))
                throw new InvalidDataException("Resume file changed during this session. Restart and review the new resume before applying.");
            var page = await browser.Open(store.DirectoryPath,"profile-update","https://www.naukri.com/mnjuser/profile",config.Email);
            var result = await NaukriProfileUpdate.Update(page,config.Name,config.ResumePath,stop.Token);
            Console.WriteLine($"[{DateTimeOffset.Now:t}] Profile: {result.Detail}");
            if (!result.Verified) { await Pause(); continue; }
            var verifiedAt = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(Path.Combine(directory,"last-profile-update.json"),System.Text.Json.JsonSerializer.Serialize(new { verifiedAt, nextDueAt=verifiedAt.AddMinutes(30), resumeFile=Path.GetFileName(config.ResumePath) }));
            nextProfileUpdate=verifiedAt.AddMinutes(30);
        }
        if (DateTimeOffset.UtcNow >= nextApply || pending is not null)
        {
            await AutoApplyRun.Run(store,new LiveAutoApplySteps(store,discovery,agent,apply),stop.Token);
            var state = store.Read(d=>d.Bot);
            Console.WriteLine($"[{DateTimeOffset.Now:t}] {state.Status}: {state.Detail}");
            if (!state.Enabled) { await Pause(); continue; }
            nextApply=state.NextRunAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
            Console.WriteLine($"Next profile update: {nextProfileUpdate.ToLocalTime():t}; next applications: {nextApply.ToLocalTime():t}. Ctrl+C stops.");
        }
        await Task.Delay(TimeSpan.FromSeconds(1),stop.Token);
    }
    async Task Pause()
    {
        store.Update(d=> { d.Bot=d.Bot with { Enabled=false, Status="needs-user-action",NextRunAt=null }; return true; });
        await browser.Diagnostics(Path.Combine(directory,"last-layout.json"));
        Console.WriteLine("PAUSED. Inspect the visible Edge window. Structural selectors saved to .NaukriTool/last-layout.json (no form values/cookies). Type RESUME after resolving login/form issues, SKIP to close a blocked pre-submit attempt, or anything else to stop. Uncertain submissions cannot be retried.");
        var command = await ReadCommand();
        if (command == "SKIP")
        {
            var active = store.Read(d => d.ApplicationAttempts.FirstOrDefault(a => a.Status is "opening" or "needs-user-action" or "ready-to-submit"));
            if (active is not null) await apply.Close(active.Id);
        }
        else if (command != "RESUME") { stop.Cancel(); return; }
        Authorize(); nextApply=DateTimeOffset.UtcNow;
    }
}
catch (OperationCanceledException) { Console.WriteLine("Stopped. A final application click already in flight may have completed; uncertain attempts are never retried automatically."); }
catch (Exception e)
{
    Console.Error.WriteLine($"Tool paused after {e.GetType().Name}: {e.Message}");
    try { await browser.Diagnostics(Path.Combine(directory,"last-layout.json")); } catch { }
    Console.WriteLine("Press Enter to close Edge after reviewing the failure. Resume/name update was not assumed successful.");
    if (!stop.IsCancellationRequested) await ReadCommand();
    Environment.ExitCode=1;
}
finally
{
    store?.Update(d=> { d.Bot=d.Bot with { Enabled=false,Status="stopped",NextRunAt=null }; return true; });
}
internal sealed class ToolClients : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
