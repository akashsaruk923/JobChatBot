using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobChatBot.Api;

public sealed class WorkspaceCatalog(IWebHostEnvironment env, IConfiguration config)
{
    public string Root { get; } = config["DataDirectory"] ?? Path.Combine(env.ContentRootPath, "App_Data");
    public WorkspaceStore Open(string id) => new(Path.Combine(Root, "sessions", id));
    public IEnumerable<WorkspaceStore> All()
    {
        var sessions = Path.Combine(Root, "sessions");
        if (!Directory.Exists(sessions)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(sessions))
            if (Regex.IsMatch(Path.GetFileName(directory), "^[a-f0-9]{64}$")) yield return new(directory);
    }
}

public sealed class WorkspaceStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new();
    private readonly object gate;
    public string DirectoryPath { get; }
    private readonly string path;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public WorkspaceStore(string directory)
    {
        DirectoryPath = directory;
        path = Path.Combine(directory, "workspace.json");
        gate = Gates.GetOrAdd(path, _ => new());
        Directory.CreateDirectory(directory);
    }
    public T Read<T>(Func<WorkspaceData, T> action) { lock (gate) return action(Load()); }
    public T Update<T>(Func<WorkspaceData, T> action)
    {
        lock (gate)
        {
            var data = Load();
            var result = action(data);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, json));
            File.Move(path + ".tmp", path, true);
            return result;
        }
    }
    private WorkspaceData Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<WorkspaceData>(File.ReadAllText(path), json) ?? throw new InvalidDataException("Invalid workspace data")
        : new();
}
