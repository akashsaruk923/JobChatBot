using System.Net.Mail;
using System.Text.RegularExpressions;
using JobChatBot.Api;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<WorkspaceCatalog>();
builder.Services.AddScoped(sp => sp.GetRequiredService<WorkspaceCatalog>().Open((string)sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.Items["WorkspaceId"]!));
builder.Services.AddScoped<ProviderSettings>();
builder.Services.AddScoped<JobAgent>();
builder.Services.AddScoped<JobDiscovery>();
builder.Services.AddSingleton<IApplyBrowser, AssistedApplyBrowser>();
builder.Services.AddScoped<AssistedApplyService>();
var keyDirectory = Path.Combine(builder.Configuration["DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data"), "keys");
Directory.CreateDirectory(keyDirectory);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(keyDirectory)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
builder.Services.AddDataProtection().SetApplicationName("JobChat").PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<NotificationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationWorker>());
builder.Services.AddHostedService<AutoApplyBot>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 3_000_000);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
var app = builder.Build();
app.Use(async (context, next) =>
{
    var id = context.Request.Cookies["jobchat-workspace"];
    if (id is null || !Regex.IsMatch(id, "^[a-f0-9]{64}$"))
    {
        id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        context.Response.Cookies.Append("jobchat-workspace", id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, MaxAge = TimeSpan.FromDays(30), Path = "/api" });
    }
    context.Items["WorkspaceId"] = id;
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "JobChat API v1"));
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}
var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    var path = context.HttpContext.Request.Path.Value ?? "";
    if (path == "/api/bot/start" || path.StartsWith("/api/application-attempts") || path.EndsWith("/apply"))
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return Results.Json(new { error = "Browser automation is available only on the local backend machine." }, statusCode: 403);
    }
    foreach (var argument in context.Arguments)
    {
        if (argument is ResumeProfile or JobInput or ChatInput)
        {
            if (argument.GetType().GetProperties().Any(p => p.PropertyType == typeof(string) && p.GetValue(argument) is null))
                return Results.BadRequest(new { error = "Text fields cannot be null. Use an empty string for optional fields." });
        }
    }
    try { return await next(context); }
    catch (ApplyFlowException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
api.MapGet("/workspace", (WorkspaceStore store, JobAgent agent, ProviderSettings settings, JobDiscovery discovery) => store.Read(d => new
{
    plan = discovery.Plan(d.Profile),
    d.Profile,
    matches = agent.Rank(d),
    d.Applications,
    d.ApplicationAttempts,
    d.Bot,
    d.Notifications,
    integrations = new { ai = agent.AiConfigured, email = settings.Has("Notifications:SendGridApiKey", "Notifications:FromEmail"), whatsApp = settings.Has("Notifications:WhatsAppToken", "Notifications:WhatsAppPhoneId", "Notifications:WhatsAppTemplate"), search = settings.Has("Search:BraveApiKey"), sendingPaused = settings["Notifications:DisableSending"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true, notificationsEnabled = settings.NotificationsEnabled, linkedIn = false, naukri = false }
}));
api.MapPut("/profile", (ResumeProfile profile, WorkspaceStore store, JobAgent agent) =>
{
    if (profile.FirstName.Length > 100 || profile.LastName.Length > 100 || profile.PhoneNumber.Length > 30 || profile.Name.Length > 100 || profile.ResumeText.Length > 50000 || profile.DesiredRole.Length > 200 || profile.Location.Length > 200)
        return Results.BadRequest(new { error = "Profile fields exceed allowed size." });
    if (profile.Email.Length > 254 || (profile.Email.Length > 0 && !MailAddress.TryCreate(profile.Email, out _)))
        return Results.BadRequest(new { error = "Enter a valid email address." });
    if (profile.PhoneNumber.Length > 0 && !Regex.IsMatch(profile.PhoneNumber, @"^\+[1-9]\d{7,14}$"))
        return Results.BadRequest(new { error = "Application phone number must include country code." });
    if (profile.WhatsApp.Length > 0 && !Regex.IsMatch(profile.WhatsApp, @"^\+[1-9]\d{7,14}$"))
        return Results.BadRequest(new { error = "WhatsApp number must include country code, e.g. +919876543210." });
    store.Update(d => { d.Profile = profile; return true; });
    return Results.Ok(new { profile, skills = agent.ExtractSkills(profile.ResumeText) });
});
api.MapPost("/jobs", (JobInput input, WorkspaceStore store) =>
{
    if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Company) || string.IsNullOrWhiteSpace(input.Description)
        || input.Title.Length > 200 || input.Company.Length > 200 || input.Location.Length > 200 || input.Description.Length > 30000 || input.Url.Length > 2048)
        return Results.BadRequest(new { error = "Title, company and description are required. Keep descriptions below 30,000 characters." });
    if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo))
        return Results.BadRequest(new { error = "Provide a valid HTTPS job listing URL." });
    return store.Update<IResult>(d =>
    {
        if (d.Jobs.Any(j => j.Url.TrimEnd('/') == input.Url.TrimEnd('/'))) return Results.Conflict(new { error = "This listing is already saved." });
        var job = new Job(Guid.NewGuid().ToString("N"), input.Title.Trim(), input.Company.Trim(), input.Location.Trim(), input.Url, input.Description, DateTimeOffset.UtcNow);
        d.Jobs.Add(job);
        return Results.Created($"/api/jobs/{job.Id}", job);
    });
});
api.MapPost("/jobs/{id}/confirm", (string id, ConfirmInput input, WorkspaceStore store) =>
{
    if (!input.SubmittedByMe) return Results.BadRequest(new { error = "Confirm only after submitting on the employer's website." });
    return store.Update<IResult>(d =>
    {
        if (!d.Jobs.Any(j => j.Id == id)) return Results.NotFound(new { error = "Job not found." });
        if (d.Applications.Any(a => a.JobId == id)) return Results.Conflict(new { error = "Application already recorded. No duplicate notifications created." });
        var application = new Application(Guid.NewGuid().ToString("N"), id, "user-confirmed", DateTimeOffset.UtcNow);
        d.Applications.Add(application);
        for (var i = 0; i < d.ApplicationAttempts.Count; i++)
            if (d.ApplicationAttempts[i].JobId == id) d.ApplicationAttempts[i] = d.ApplicationAttempts[i] with { Status = "user-confirmed", Detail = "The person confirmed submission after checking the original site. Not a platform API receipt.", ReviewToken = null, Fields = null, UpdatedAt = DateTimeOffset.UtcNow };
        foreach (var channel in new[] { "email", "whatsapp" })
            d.Notifications.Add(new(Guid.NewGuid().ToString("N"), application.Id, channel, "pending", "Waiting for notification worker. User confirmation, not platform verification.", DateTimeOffset.UtcNow));
        return Results.Created($"/api/applications/{application.Id}", application);
    });
});
api.MapPost("/chat", async (ChatInput input, WorkspaceStore store, JobAgent agent, JobDiscovery discovery, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 4000) return Results.BadRequest(new { error = "Enter a message of up to 4,000 characters." });
    if (Regex.IsMatch(input.Message, @"\b(search|find|look for)\b.*\b(jobs|roles|openings|opportunities)\b|नोकरी.*शोध|जॉब.*शोध", RegexOptions.IgnoreCase))
    {
        var result = await discovery.Search(store, ct);
        return Results.Ok(new { reply = result.Message + " Target: " + result.Plan.Role + ". Open Find jobs to review your search links and results.", mode = "search-agent", search = result });
    }
    return Results.Ok(await agent.Chat(input.Message, store.Read(d => d), ct));
});
api.MapGet("/automation/capabilities", (IApplyBrowser browser) => Results.Ok(new { enabled = browser.Enabled, browser = "Installed Microsoft Edge on local Windows", login = "Saved browser session or credentials in Connections. CAPTCHA/OTP stops automation.", linkedIn = "Conservative English Easy Apply forms", naukri = "Existing-profile Apply, with recognized receipt required", liveSiteVerified = false }));
api.MapPost("/jobs/{id}/apply", async (string id, AssistedApplyService service) => Results.Ok(await service.Open(id)));
api.MapGet("/application-attempts", (WorkspaceStore store) => Results.Ok(store.Read(d => d.ApplicationAttempts)));
api.MapPost("/application-attempts/{id}/continue", async (string id, AssistedApplyService service) => Results.Ok(await service.Continue(id)));
api.MapPost("/application-attempts/{id}/submit", async (string id, SubmitApplicationInput input, AssistedApplyService service) => Results.Ok(await service.Submit(id, input)));
api.MapPost("/application-attempts/{id}/verify", async (string id, AssistedApplyService service) => Results.Ok(await service.Verify(id)));
api.MapPost("/application-attempts/{id}/not-submitted", async (string id, ResolveUnknownInput input, AssistedApplyService service) => Results.Ok(await service.ResolveNotSubmitted(id, input)));
api.MapDelete("/application-attempts/{id}/browser", async (string id, AssistedApplyService service) => Results.Ok(await service.Close(id)));
api.MapGet("/bot", (WorkspaceStore store, IApplyBrowser browser) => Results.Ok(new { state = store.Read(d => d.Bot), browserAvailable = browser.Enabled, liveSiteVerified = false }));
api.MapPost("/bot/start", (BotPolicy input, WorkspaceStore store, IApplyBrowser browser, ProviderSettings settings, JobDiscovery discovery) =>
{
    if (!input.AuthorizeSubmission || input.MinimumMatchScore < 0 || input.MinimumMatchScore > 100 || input.MaxApplicationsPerRun < 1 || input.MaxApplicationsPerRun > 10 || input.IntervalMinutes < 30 || input.IntervalMinutes > 1440)
        return Results.BadRequest(new { error = "Authorize real submissions explicitly. Match score must be 0–100, maximum applications 1–10 per run and interval 30–1440 minutes." });
    var data = store.Read(d => d);
    if (data.Bot.Enabled) return Results.Conflict(new { error = "Bot is already running. Stop it before changing policy." });
    if (string.IsNullOrWhiteSpace(data.Profile.Name) || !MailAddress.TryCreate(data.Profile.Email, out _) || string.IsNullOrWhiteSpace(data.Profile.ResumeText) || string.IsNullOrWhiteSpace(discovery.Plan(data.Profile).Role))
        return Results.BadRequest(new { error = "Save your name, valid email, resume and target role before starting." });
    if (!settings.Has("Search:BraveApiKey")) return Results.Conflict(new { error = "Configure the Brave Search API key in Connections first. No jobs were searched or applied to." });
    if (!browser.Enabled) return Results.Conflict(new { error = "Run this backend locally on Windows with installed Edge. The cloud backend cannot use your Windows login/browser." });
    var state = store.Update(d => { d.Bot = new(true, "queued", "Authorized run queued. Search and bounded submissions start shortly. CAPTCHA/OTP or unknown questions pause the bot.", input, AssistedApplyService.ProfileHash(d.Profile), DateTimeOffset.UtcNow, AuthorizationId: Guid.NewGuid().ToString("N")); return d.Bot; });
    return Results.Ok(state);
});
api.MapPost("/bot/stop", (WorkspaceStore store) => Results.Ok(store.Update(d => { d.Bot = d.Bot with { Enabled = false, Status = "stopped", Detail = "Stop requested. An in-flight Submit may still complete; no further applications will be started.", NextRunAt = null }; return d.Bot; })));
api.MapGet("/connections", (ProviderSettings settings) => Results.Ok(settings.Status()));
api.MapPut("/connections", (CredentialInput input, ProviderSettings settings, WorkspaceStore store) =>
{
    if (input.Values is null || input.Values.Any(v => !ProviderSettings.Keys.Contains(v.Key) || v.Value?.Length > 2000 || (v.Value?.Any(char.IsControl) ?? false)))
        return Results.BadRequest(new { error = "Unknown setting or invalid value. Each setting must be at most 2,000 characters without control characters." });
    if (input.Values.TryGetValue("Ai:BaseUrl", out var endpoint) && !string.IsNullOrWhiteSpace(endpoint)
        && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0))
        return Results.BadRequest(new { error = "AI Base URL must be an HTTPS URL without embedded credentials." });
    settings.Save(input);
    store.Update(d => { if (d.Bot.Enabled) d.Bot = d.Bot with { Enabled = false, Status = "stopped", Detail = "Connection settings changed. Review them and restart the bot.", NextRunAt = null }; return true; });
    return Results.Ok(settings.Status());
});
api.MapDelete("/connections", (ProviderSettings settings, WorkspaceStore store) => { settings.Clear(); store.Update(d => { d.Bot = d.Bot with { Enabled = false, Status = "stopped", Detail = "Connection settings removed.", NextRunAt = null }; return true; }); return Results.Ok(settings.Status()); });
api.MapPost("/search", async (WorkspaceStore store, JobDiscovery discovery, CancellationToken ct) => Results.Ok(await discovery.Search(store, ct)));
api.MapPost("/resume/extract", async (IFormFile file, WorkspaceStore store, CancellationToken ct) =>
{
    if (file.Length == 0 || file.Length > 2 * 1024 * 1024) return Results.BadRequest(new { error = "Upload a resume between 1 byte and 2 MB." });
    try
    {
        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        var text = ResumeReader.Extract(bytes, file.FileName);
        var storedName = "resume-" + Guid.NewGuid().ToString("N") + Path.GetExtension(file.FileName).ToLowerInvariant();
        var path = Path.Combine(store.DirectoryPath, storedName);
        await File.WriteAllBytesAsync(path, bytes, ct);
        var old = store.Update(d =>
        {
            var previous = d.ResumeAsset;
            if (d.Bot.Enabled) d.Bot = d.Bot with { Enabled = false, Status = "stopped", Detail = "A new resume was uploaded. Save/review the profile and start again.", NextRunAt = null };
            for (var i = 0; i < d.ApplicationAttempts.Count; i++)
                if (d.ApplicationAttempts[i].Status == "ready-to-submit") d.ApplicationAttempts[i] = d.ApplicationAttempts[i] with { Status = "needs-user-action", Detail = "Resume upload changed. Continue and review the attached file again.", ReviewToken = null, Fields = null };
            d.ResumeAsset = new(storedName, Path.GetFileName(file.FileName), AssistedApplyService.TextHash(text)); return previous;
        });
        if (old is not null && Path.GetFileName(old.StoredName) == old.StoredName) File.Delete(Path.Combine(store.DirectoryPath, old.StoredName));
        return Results.Ok(new { text });
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is not OperationCanceledException) { return Results.BadRequest(new { error = "Could not read this resume. Use a valid PDF, DOCX or UTF-8 text file. Scanned/encrypted PDFs may require pasting text manually." }); }
}).DisableAntiforgery();
app.Run();
