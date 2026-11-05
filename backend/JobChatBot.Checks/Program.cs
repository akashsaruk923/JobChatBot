using System.Net;
using System.Text;
using System.Text.Json;
using JobChatBot.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

var directory = Path.Combine(Path.GetTempPath(), "jobchat-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var count = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
try
{
    var store = new WorkspaceStore(Path.Combine(directory, "a"));
    var other = new WorkspaceStore(Path.Combine(directory, "b"));
    var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
    var config = new ConfigurationBuilder().Build();
    var settings = new ProviderSettings(store, protection, config);
    var otherSettings = new ProviderSettings(other, protection, config);
    settings.Save(new(new() { ["Search:BraveApiKey"] = "test-search-key", ["Ai:ApiKey"] = "test-ai-key", ["Ai:Model"] = "test-model" }));
    Check(!otherSettings.Has("Search:BraveApiKey"), "Another workspace must not inherit saved credentials");
    settings.Save(new(new() { ["Ai:ApiKey"] = "" }));
    Check(settings["Ai:ApiKey"] == "test-ai-key", "Blank credential must preserve saved value");
    Check(!settings.NotificationsEnabled, "Sending must default off");
    var factory = new FakeFactory();
    var agent = new JobAgent(factory, settings);
    var discovery = new JobDiscovery(agent, settings, factory);
    var plan = discovery.Plan(new(ResumeText: "Python REST Docker", Location: "Mumbai"));
    Check(plan.Role == "Python Developer" && plan.Location == "Mumbai", "Resume should infer Python role instead of a fixed .NET role");
    Check(!plan.Query.Contains("test-ai-key"), "Search plan must not contain credentials");
    store.Update(d => { d.Profile = new("Candidate", "private@example.com", "+919999999999", "Python REST Docker", "", "Mumbai"); return true; });
    factory.Reply = request =>
    {
        Check(request.RequestUri!.Host == "api.search.brave.com", "Search must use expected provider");
        Check(request.Headers.Contains("X-Subscription-Token"), "Search key must be carried in a header");
        Check(!request.RequestUri.Query.Contains("private") && !request.RequestUri.Query.Contains("test-search-key"), "Search query must exclude contact and secret data");
        return Json("""{"web":{"results":[{"title":"Python Developer","url":"https://www.linkedin.com/jobs/view/123","description":"<b>Python</b> REST"},{"title":"Python Engineer","url":"https://www.naukri.com/job-listings-python-123","description":"Docker Python"},{"title":"Unsafe listing","url":"https://linkedin.com.evil.example/jobs/view/1","description":"do not import"},{"title":"Search index","url":"https://www.linkedin.com/jobs/search/","description":"not an individual listing"}]}}""");
    };
    var found = await discovery.Search(store, default);
    Check(found.Status == "complete" && found.Added == 2, "Only supported direct listing URLs should be imported");
    Check(store.Read(d => d.Jobs.All(j => j.DescriptionKind == "search-snippet" && !j.Description.Contains("<b>"))), "Snippet provenance and HTML stripping required");
    Check((await discovery.Search(store, default)).Added == 0, "Repeat search must not create duplicates");
    factory.Reply = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
    Check((await discovery.Search(store, default)).Status == "provider-error", "Provider rejection must be visible");
    factory.Reply = _ => Json("not json");
    Check((await discovery.Search(store, default)).Status == "provider-error", "Invalid provider response must not claim success");
    factory.Reply = request =>
    {
        var content = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Check(!content.Contains("private@example.com") && !content.Contains("919999999999"), "Dedicated contacts must not be in AI context");
        return Json("""{"choices":[{"message":{"content":"Review your Python matches."}}]}""");
    };
    var chat = JsonSerializer.Serialize(await agent.Chat("Which matches fit?", store.Read(d => d), default));
    Check(chat.Contains("Review your Python matches.") && chat.Contains("\"mode\":\"ai\""), ".NET agent should consume an AI response");
    var workerConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = Path.Combine(directory, "worker") }).Build();
    var catalog = new WorkspaceCatalog(new FakeEnvironment(directory), workerConfig);
    foreach (var person in new[] { ('a', "alice@example.com"), ('b', "bob@example.com") })
    {
        var candidate = catalog.Open(new string(person.Item1, 64));
        candidate.Update(d =>
        {
            d.Profile = new(Name: person.Item1.ToString(), Email: person.Item2);
            d.Jobs.Add(new("job", "Developer", "Test", "", "https://example.com/job", "", DateTimeOffset.UtcNow));
            d.Applications.Add(new("app", "job", "user-confirmed", DateTimeOffset.UtcNow));
            d.Notifications.Add(new("notice", "app", "email", "pending", "", DateTimeOffset.UtcNow));
            return true;
        });
        new ProviderSettings(candidate, protection, workerConfig).Save(new(new() { ["Notifications:SendGridApiKey"] = "fake-" + person.Item1, ["Notifications:FromEmail"] = "sender@example.com" }, true));
    }
    // Exercise the real keyless discovery path with HTTP fixtures, never live accounts.
    var keylessStore = new WorkspaceStore(Path.Combine(directory, "keyless"));
    keylessStore.Update(d => { d.Profile = new(ResumeText: "Python REST", DesiredRole: "Python Developer", Location: "Pune"); return true; });
    var keylessSettings = new ProviderSettings(keylessStore, protection, new ConfigurationBuilder().Build());
    var keylessSearch = new JobDiscovery(new JobAgent(factory, keylessSettings), keylessSettings, factory);
    const string cards = """
    <li><a href="https://in.linkedin.com/jobs/view/python-developer-123?trackingId=test"></a><h3 class="base-search-card__title">Python &amp; REST Developer</h3><h4 class="base-search-card__subtitle"><a>Example Company</a></h4><span class="job-search-card__location">Pune, Maharashtra</span></li>
    <li><a href="https://in.linkedin.com:9443/jobs/view/invalid-port"></a><h3 class="base-search-card__title">Invalid port</h3></li>
    <li><a href="https://in.linkedin.com.evil.example/jobs/view/fake-region"></a><h3 class="base-search-card__title">Fake region</h3></li>
    <li><a href="https://linkedin.com.evil.example/jobs/view/456"></a><h3 class="base-search-card__title">Fake listing</h3></li>
    <li><a href="https://www.linkedin.com/jobs/view/python-developer-123?trackingId=duplicate"></a><h3 class="base-search-card__title">Duplicate</h3></li>
    """;
    factory.Reply = request =>
    {
        Check(request.RequestUri!.Host == "www.linkedin.com" && request.RequestUri.AbsolutePath == "/jobs-guest/jobs/api/seeMoreJobPostings/search", "Keyless search uses public guest listings");
        Check(!request.Headers.Contains("X-Subscription-Token") && request.Headers.Authorization is null, "Keyless search sends no API key");
        Check(!request.RequestUri.Query.Contains("REST") && request.RequestUri.Query.Contains("Pune"), "Only target role and location are sent");
        return new(HttpStatusCode.OK) { Content = new StringContent(cards) };
    };
    Check((await keylessSearch.Search(keylessStore, default)).Added == 1, "Keyless search imports only valid unique listing URLs");
    var publicJob = keylessStore.Read(d => d.Jobs.Single());
    Check(publicJob.Title == "Python & REST Developer" && publicJob.Company == "Example Company" && publicJob.Location == "Pune, Maharashtra", "Public listing fields are decoded and preserved");
    Check(publicJob.Url == "https://www.linkedin.com/jobs/view/python-developer-123", "Country-host listings normalize to the supported canonical apply origin");
    Check(publicJob.DescriptionKind == "public-listing-card" && !publicJob.Url.Contains('?'), "Card provenance is explicit and tracking queries are removed");
    Check((await keylessSearch.Search(keylessStore, default)).Added == 0, "Repeated keyless searches deduplicate saved jobs");
    factory.Reply = _ => new(HttpStatusCode.TooManyRequests);
    Check((await keylessSearch.Search(keylessStore, default)).Status == "provider-error", "Rate limits are surfaced without bypass or invented success");
    factory.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in or verify</html>") };
    Check((await keylessSearch.Search(keylessStore, default)).Status == "provider-error", "Unrecognized pages do not claim successful discovery");
    factory.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("") };
    Check((await keylessSearch.Search(keylessStore, default)).Status == "complete", "Empty guest results are handled");
    var recipients = new List<string>();
    factory.Reply = request =>
    {
        var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(payload);
        var recipient = doc.RootElement.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString()!;
        recipients.Add(recipient);
        Check(request.Headers.Authorization!.Parameter == (recipient.StartsWith("alice") ? "fake-a" : "fake-b"), "Notification must use the recipient workspace's credentials");
        return new HttpResponseMessage(HttpStatusCode.Accepted);
    };
    using var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(workerConfig).AddSingleton<IDataProtectionProvider>(protection).BuildServiceProvider();
    using var worker = new NotificationWorker(catalog, services.GetRequiredService<IServiceScopeFactory>(), factory, services.GetRequiredService<ILogger<NotificationWorker>>(), workerConfig);
    await worker.StartAsync(default);
    for (var i = 0; i < 50 && recipients.Count < 2; i++) await Task.Delay(20);
    await worker.StopAsync(default);
    Check(recipients.Order().SequenceEqual(new[] { "alice@example.com", "bob@example.com" }), "Notification recipient isolation");
    Check(catalog.All().All(w => w.Read(d => d.Notifications.Single().Status == "provider-accepted")), "Provider acceptance must be persisted, not mislabeled delivered");
    foreach (var candidate in catalog.All()) candidate.Update(d => { d.Notifications[0] = d.Notifications[0] with { Status = "pending" }; return true; });
    var pausedConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:DisableSending"] = "true" }).Build();
    factory.Reply = _ => throw new Exception("Paused server must never call a notification provider");
    using var paused = new NotificationWorker(catalog, services.GetRequiredService<IServiceScopeFactory>(), factory, services.GetRequiredService<ILogger<NotificationWorker>>(), pausedConfig);
    await paused.StartAsync(default);
    await Task.Delay(100);
    await paused.StopAsync(default);
    Check(catalog.All().All(w => w.Read(d => d.Notifications.Single().Status == "awaiting-configuration")), "Server pause must prevent provider calls even with valid settings and queued messages");
    settings.Clear();
    Check(!settings.Has("Ai:ApiKey") && !settings.NotificationsEnabled, "Removing settings must remove keys and disable sending");
    count += await JobChatBot.Checks.BotChecks.Run(directory);
    if (args.Contains("--browser")) count += await BrowserChecks.Run();
    Console.WriteLine($"PASS: {count} isolated provider/agent checks using fake HTTP responses. No external calls.");
}
finally { Directory.Delete(directory, true); }

static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
sealed class FakeFactory : IHttpClientFactory
{
    public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => throw new Exception("Unexpected provider call");
    public HttpClient CreateClient(string name) => new(new Handler(this));
    private sealed class Handler(FakeFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(owner.Reply(request));
    }
}

sealed class FakeEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Checks";
    public string EnvironmentName { get; set; } = "Production";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
