using System.Net.Mail;
using System.Text.RegularExpressions;
using JobChatBot.Api;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<WorkspaceCatalog>();
builder.Services.AddScoped(sp => sp.GetRequiredService<WorkspaceCatalog>().Open((string)sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.Items["WorkspaceId"]!));
builder.Services.AddScoped<ProviderSettings>();
builder.Services.AddScoped<JobAgent>();
builder.Services.AddScoped<JobDiscovery>();
var keyDirectory = Path.Combine(builder.Configuration["DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data"), "keys");
Directory.CreateDirectory(keyDirectory);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(keyDirectory)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
builder.Services.AddDataProtection().SetApplicationName("JobChat").PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<NotificationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationWorker>());
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
var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    foreach (var argument in context.Arguments)
    {
        if (argument is ResumeProfile or JobInput or ChatInput)
        {
            if (argument.GetType().GetProperties().Any(p => p.PropertyType == typeof(string) && p.GetValue(argument) is null))
                return Results.BadRequest(new { error = "Text fields cannot be null. Use an empty string for optional fields." });
        }
    }
    return await next(context);
});
api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
api.MapGet("/workspace", (WorkspaceStore store, JobAgent agent, ProviderSettings settings, JobDiscovery discovery) => store.Read(d => new
{
    plan = discovery.Plan(d.Profile),
    d.Profile,
    matches = agent.Rank(d),
    d.Applications,
    d.Notifications,
    integrations = new { ai = agent.AiConfigured, email = settings.Has("Notifications:SendGridApiKey", "Notifications:FromEmail"), whatsApp = settings.Has("Notifications:WhatsAppToken", "Notifications:WhatsAppPhoneId", "Notifications:WhatsAppTemplate"), search = settings.Has("Search:BraveApiKey"), sendingPaused = settings["Notifications:DisableSending"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true, notificationsEnabled = settings.NotificationsEnabled, linkedIn = false, naukri = false }
}));
api.MapPut("/profile", (ResumeProfile profile, WorkspaceStore store, JobAgent agent) =>
{
    if (profile.Name.Length > 100 || profile.ResumeText.Length > 50000 || profile.DesiredRole.Length > 200 || profile.Location.Length > 200)
        return Results.BadRequest(new { error = "Profile fields exceed allowed size." });
    if (profile.Email.Length > 254 || (profile.Email.Length > 0 && !MailAddress.TryCreate(profile.Email, out _)))
        return Results.BadRequest(new { error = "Enter a valid email address." });
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
api.MapPost("/jobs/{id}/apply", (string id) => Results.Conflict(new { error = "Automated submission is not connected. Apply on the original listing, then confirm your submission here." }));
api.MapGet("/connections", (ProviderSettings settings) => Results.Ok(settings.Status()));
api.MapPut("/connections", (CredentialInput input, ProviderSettings settings) =>
{
    if (input.Values is null || input.Values.Any(v => !ProviderSettings.Keys.Contains(v.Key) || v.Value?.Length > 2000 || (v.Value?.Any(char.IsControl) ?? false)))
        return Results.BadRequest(new { error = "Unknown setting or invalid value. Each setting must be at most 2,000 characters without control characters." });
    if (input.Values.TryGetValue("Ai:BaseUrl", out var endpoint) && !string.IsNullOrWhiteSpace(endpoint)
        && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0))
        return Results.BadRequest(new { error = "AI Base URL must be an HTTPS URL without embedded credentials." });
    settings.Save(input);
    return Results.Ok(settings.Status());
});
api.MapDelete("/connections", (ProviderSettings settings) => { settings.Clear(); return Results.Ok(settings.Status()); });
api.MapPost("/search", async (WorkspaceStore store, JobDiscovery discovery, CancellationToken ct) => Results.Ok(await discovery.Search(store, ct)));
api.MapPost("/resume/extract", async (IFormFile file, CancellationToken ct) =>
{
    if (file.Length == 0 || file.Length > 2 * 1024 * 1024) return Results.BadRequest(new { error = "Upload a resume between 1 byte and 2 MB." });
    try
    {
        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, ct);
        return Results.Ok(new { text = ResumeReader.Extract(memory.ToArray(), file.FileName) });
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is not OperationCanceledException) { return Results.BadRequest(new { error = "Could not read this resume. Use a valid PDF, DOCX or UTF-8 text file. Scanned/encrypted PDFs may require pasting text manually." }); }
}).DisableAntiforgery();
app.Run();
