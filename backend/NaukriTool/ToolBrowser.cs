using JobChatBot.Api;
using Microsoft.Playwright;
internal sealed class ToolBrowser(string directory) : IApplyBrowser, IAsyncDisposable
{
    private IPlaywright? engine;
    private IBrowserContext? context;
    private IPage? page;
    private string? attempt;
    private readonly SemaphoreSlim gate = new(1,1);
    public bool Enabled => OperatingSystem.IsWindows();
    public SemaphoreSlim Gate(string _) => gate;
    public IPage? Page(string _, string id) => attempt == id && page is { IsClosed:false } ? page : null;
    public async Task<IPage> Open(string _, string id, string url, string identity)
    {
        if (!Enabled) throw new InvalidOperationException("Run NaukriTool on Windows with installed Microsoft Edge.");
        if (context is null)
        {
            engine = await Playwright.CreateAsync();
            context = await engine.Chromium.LaunchPersistentContextAsync(Path.Combine(directory,"edge-session"), new() { Channel="msedge", Headless=false, ViewportSize=new() { Width=1280,Height=900 }, AcceptDownloads=false });
        }
        page = page is { IsClosed:false } ? page : context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        attempt = id;
        page.SetDefaultTimeout(10000);
        await page.GotoAsync(url, new() { WaitUntil=WaitUntilState.DOMContentLoaded, Timeout=45000 });
        return page;
    }
    public Task Close(string _) { attempt=null; return Task.CompletedTask; } // Keep the same visible, logged-in browser for the next job.
    public async Task Diagnostics(string path)
    {
        if (page is null || page.IsClosed) return;
        // Structural selectors only: no text, field values, cookies, page HTML or credentials.
        var shape = await page.EvaluateAsync<string>("""() => JSON.stringify([...document.querySelectorAll('input,button,textarea,[role=dialog],article')].slice(0,300).map(e=>({tag:e.tagName,id:e.id,class:e.className,type:e.getAttribute('type')})),null,2)""");
        await File.WriteAllTextAsync(path, shape);
    }
    public async ValueTask DisposeAsync()
    {
        if (context is not null) await context.CloseAsync();
        engine?.Dispose(); gate.Dispose();
    }
}
