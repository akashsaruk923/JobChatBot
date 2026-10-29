using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace JobChatBot.Api;

public sealed class NotificationWorker(WorkspaceCatalog catalog, IServiceScopeFactory scopes, IHttpClientFactory clients, ILogger<NotificationWorker> log, IConfiguration host) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var store in catalog.All())
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var protection = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
                    var config = new ProviderSettings(store, protection, scope.ServiceProvider.GetRequiredService<IConfiguration>());
                    if (host.GetValue<bool>("Notifications:DisableSending") || !config.NotificationsEnabled)
                    {
                        foreach (var notice in store.Read(d => d.Notifications.Where(n => n.Status == "pending").ToArray()))
                            Set(store, notice.Id, "awaiting-configuration", "Notification sending is disabled by workspace or server. Review Connections and server development settings.");
                        continue;
                    }
                    foreach (var item in store.Read(d => d.Notifications.Where(n => n.Status is "pending" or "awaiting-configuration").ToArray()))
                    {
                        var context = store.Read(d => new { d.Profile, Job = d.Jobs.First(j => j.Id == d.Applications.First(a => a.Id == item.ApplicationId).JobId) });
                        var ready = item.Channel == "email" ? config.Has("Notifications:SendGridApiKey", "Notifications:FromEmail") && context.Profile.Email.Length > 0 : config.Has("Notifications:WhatsAppToken", "Notifications:WhatsAppPhoneId", "Notifications:WhatsAppTemplate") && context.Profile.WhatsApp.Length > 0;
                        if (!ready)
                        {
                            if (item.Status != "awaiting-configuration") Set(store, item.Id, "awaiting-configuration", "Provider configuration or recipient is missing. No message sent.");
                            continue;
                        }
                        Set(store, item.Id, "sending", "Provider request in progress. Delivery not confirmed.");
                        try
                        {
                            using var request = item.Channel == "email"
                                ? new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send")
                                : new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/v22.0/{Uri.EscapeDataString(config["Notifications:WhatsAppPhoneId"]!)}/messages");
                            if (item.Channel == "email")
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Notifications:SendGridApiKey"]);
                                request.Content = JsonContent.Create(new
                                {
                                    personalizations = new[] { new { to = new[] { new { email = context.Profile.Email } } } },
                                    from = new { email = config["Notifications:FromEmail"] },
                                    subject = "Job application recorded",
                                    content = new[] { new { type = "text/plain", value = $"You confirmed applying for {context.Job.Title} at {context.Job.Company}. {context.Job.Url}" } }
                                });
                            }
                            else
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Notifications:WhatsAppToken"]);
                                request.Content = JsonContent.Create(new
                                {
                                    messaging_product = "whatsapp",
                                    to = context.Profile.WhatsApp.TrimStart('+'),
                                    type = "template",
                                    template = new
                                    {
                                        name = config["Notifications:WhatsAppTemplate"],
                                        language = new { code = config["Notifications:WhatsAppLanguage"] ?? "en_US" },
                                        components = new[] { new { type = "body", parameters = new[] { new { type = "text", text = context.Job.Title }, new { type = "text", text = context.Job.Company }, new { type = "text", text = context.Job.Url } } } }
                                    }
                                });
                            }
                            var client = clients.CreateClient();
                            client.Timeout = TimeSpan.FromSeconds(30);
                            using var response = await client.SendAsync(request, ct);
                            Set(store, item.Id, response.IsSuccessStatusCode ? "provider-accepted" : "failed", response.IsSuccessStatusCode
                                ? "Provider accepted the message. Final delivery is not tracked."
                                : $"Provider returned HTTP {(int)response.StatusCode}. Check settings. No automatic retry.");
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or InvalidOperationException)
                        {
                            Set(store, item.Id, "delivery-unknown", "Provider acceptance unknown after network interruption. No automatic retry to avoid duplicates.");
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("Workspace notification processing failed ({Type}).", ex.GetType().Name);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }
    private static void Set(WorkspaceStore store, string id, string status, string detail) => store.Update(d =>
    {
        var index = d.Notifications.FindIndex(n => n.Id == id);
        d.Notifications[index] = d.Notifications[index] with { Status = status, Detail = detail };
        return true;
    });
}
