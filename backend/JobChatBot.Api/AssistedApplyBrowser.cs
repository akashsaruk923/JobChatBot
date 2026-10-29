using System.Collections.Concurrent;
using Microsoft.Playwright;
namespace JobChatBot.Api;

public interface IApplyBrowser
{
    bool Enabled { get; }
    SemaphoreSlim Gate(string workspace);
    IPage? Page(string workspace, string attemptId);
    Task<IPage> Open(string workspace, string attemptId, string url, string identity);
    Task Close(string workspace);
}
public sealed class AssistedApplyBrowser(IConfiguration config) : IApplyBrowser, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly Dictionary<string, (string AttemptId, IBrowserContext Context, IPage Page)> sessions = new();
    private readonly Lazy<Task<IPlaywright>> engine = new(Playwright.CreateAsync);
    public bool Enabled => OperatingSystem.IsWindows() && config.GetValue("BrowserApply:Enabled", true);
    public SemaphoreSlim Gate(string workspace) => gates.GetOrAdd(workspace, _ => new(1, 1));
    public IPage? Page(string workspace, string attemptId)
    {
        lock (sessions) return sessions.TryGetValue(workspace, out var value) && value.AttemptId == attemptId && !value.Page.IsClosed ? value.Page : null;
    }
    public async Task<IPage> Open(string workspace, string attemptId, string url, string identity)
    {
        if (!Enabled) throw new ApplyFlowException(409, "Assisted apply runs on your local Windows backend with installed Microsoft Edge. It cannot open your Windows browser from a cloud server.");
        await Close(workspace);
        var playwright = await engine.Value;
        var context = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(workspace, "browser", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))), new()
        {
            Channel = "msedge",
            Headless = false,
            AcceptDownloads = false,
            ViewportSize = new() { Width = 1280, Height = 900 }
        });
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        lock (sessions) sessions[workspace] = (attemptId, context, page);
        page.SetDefaultTimeout(5000);
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
        return page;
    }
    public async Task Close(string workspace)
    {
        IBrowserContext? context = null;
        lock (sessions) { if (sessions.Remove(workspace, out var entry)) context = entry.Context; }
        if (context is not null) { try { await context.CloseAsync(); } catch (PlaywrightException) { } }
    }
    public async ValueTask DisposeAsync()
    {
        string[] workspaces;
        lock (sessions) workspaces = sessions.Keys.ToArray();
        foreach (var workspace in workspaces) await Close(workspace);
        if (engine.IsValueCreated) { try { (await engine.Value).Dispose(); } catch (PlaywrightException) { } }
    }
}
public sealed class ApplyFlowException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
