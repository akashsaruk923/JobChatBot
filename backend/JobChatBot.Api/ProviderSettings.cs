using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace JobChatBot.Api;

public record CredentialInput(Dictionary<string, string?> Values, bool EnableNotifications = false);
public sealed class ProviderSettings(WorkspaceStore store, IDataProtectionProvider protection, IConfiguration server)
{
    public static readonly string[] Keys = ["Ai:ApiKey", "Ai:Model", "Ai:BaseUrl", "Search:BraveApiKey", "Notifications:SendGridApiKey", "Notifications:FromEmail", "Notifications:WhatsAppToken", "Notifications:WhatsAppPhoneId", "Notifications:WhatsAppTemplate", "Notifications:WhatsAppLanguage"];
    private static readonly ConcurrentDictionary<string, object> Gates = new();
    private object Gate => Gates.GetOrAdd(store.DirectoryPath, _ => new());
    private string PathName => Path.Combine(store.DirectoryPath, "credentials.protected");
    private IDataProtector Protector => protection.CreateProtector("JobChat.Credentials.v1", Path.GetFileName(store.DirectoryPath));
    public string? this[string key] => Read().GetValueOrDefault(key) is { Length: > 0 } value ? value : server[key];
    public bool Has(params string[] keys) => keys.All(k => !string.IsNullOrWhiteSpace(this[k]));
    public bool NotificationsEnabled => Read().GetValueOrDefault("Notifications:Enabled") == "true";
    public object Status() => new { fields = Keys.Select(key => new { key, configured = !string.IsNullOrWhiteSpace(this[key]) }), enableNotifications = NotificationsEnabled, sendingPaused = server.GetValue<bool>("Notifications:DisableSending") };
    public void Save(CredentialInput input)
    {
        lock (Gate)
        {
            var values = Read();
            foreach (var entry in input.Values)
                if (Keys.Contains(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value)) values[entry.Key] = entry.Value.Trim();
            values["Notifications:Enabled"] = input.EnableNotifications ? "true" : "false";
            File.WriteAllText(PathName + ".tmp", Protector.Protect(JsonSerializer.Serialize(values)));
            File.Move(PathName + ".tmp", PathName, true);
        }
    }
    public void Clear() { lock (Gate) { if (File.Exists(PathName)) File.Delete(PathName); } }
    private Dictionary<string, string> Read()
    {
        lock (Gate) return File.Exists(PathName)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(Protector.Unprotect(File.ReadAllText(PathName))) ?? new()
            : new();
    }
}
