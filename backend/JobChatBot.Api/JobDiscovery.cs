using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobChatBot.Api;

public record SearchPlan(string Role, string Location, string[] Skills, string Query, string LinkedInUrl, string NaukriUrl);
public record DiscoveryResult(string Status, SearchPlan Plan, int Added, string Message);
public sealed class JobDiscovery(JobAgent agent, ProviderSettings config, IHttpClientFactory clients)
{
    public SearchPlan Plan(ResumeProfile profile)
    {
        var skills = agent.ExtractSkills(profile.ResumeText);
        var role = profile.DesiredRole.Trim();
        if (role.Length == 0)
        {
            role = skills.Contains(".NET") || skills.Contains("C#") ? ".NET Developer"
                : skills.Contains("Angular") ? "Angular Developer"
                : skills.Contains("React") ? "React Developer"
                : skills.Contains("Python") ? "Python Developer"
                : skills.Contains("Java") ? "Java Developer"
                : skills.Length > 0 ? string.Join(" ", skills.Take(2)) + " jobs" : "";
        }
        var location = profile.Location.Trim();
        var keywords = string.Join(" ", new[] { role }.Concat(skills.Where(s => !role.Contains(s, StringComparison.OrdinalIgnoreCase)).Take(3)));
        var query = $"{keywords} {location} (site:linkedin.com/jobs/view/ OR site:naukri.com/job-listings-)".Trim();
        var slug = Regex.Replace(role.ToLowerInvariant().Replace(".net", "dot net"), "[^a-z0-9]+", "-").Trim('-');
        var city = Regex.Replace(location.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return new(role, location, skills, query,
            $"https://www.linkedin.com/jobs/search/?keywords={Uri.EscapeDataString(keywords)}&location={Uri.EscapeDataString(location)}",
            $"https://www.naukri.com/{slug}-jobs" + (city.Length > 0 ? $"-in-{city}" : ""));
    }
    public async Task<DiscoveryResult> Search(WorkspaceStore store, CancellationToken ct)
    {
        var profile = store.Read(d => d.Profile);
        var plan = Plan(profile);
        if (string.IsNullOrWhiteSpace(profile.ResumeText) || string.IsNullOrWhiteSpace(plan.Role))
            return new("needs-profile", plan, 0, "Save your resume and preferred role first. Search uses the saved profile for this browser workspace.");
        if (!config.Has("Search:BraveApiKey"))
            return new("needs-credentials", plan, 0, "Add a Brave Search API key in Connections to retrieve indexed LinkedIn/Naukri listings. Personalized search links are ready to use without a key.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(plan.Query) + "&count=10");
            request.Headers.Add("X-Subscription-Token", config["Search:BraveApiKey"]);
            var client = clients.CreateClient(); client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new("provider-error", plan, 0, $"Search provider returned HTTP {(int)response.StatusCode}. Check the API key, quota and network access. No results were invented.");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var found = new List<Job>();
            if (body.RootElement.TryGetProperty("web", out var web) && web.TryGetProperty("results", out var results))
            {
                foreach (var result in results.EnumerateArray())
                {
                    var url = result.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) continue;
                    var linkedIn = (uri.Host == "linkedin.com" || uri.Host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.StartsWith("/jobs/view/");
                    var naukri = (uri.Host == "naukri.com" || uri.Host.EndsWith(".naukri.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.StartsWith("/job-listings-");
                    if (!linkedIn && !naukri) continue;
                    var title = Plain(result.TryGetProperty("title", out var t) ? t.GetString() ?? "Job listing" : "Job listing");
                    var description = Plain(result.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "");
                    found.Add(new(Guid.NewGuid().ToString("N"), title[..Math.Min(200, title.Length)], "Company not provided — check listing", "", uri.AbsoluteUri, description[..Math.Min(30000, description.Length)], DateTimeOffset.UtcNow, linkedIn ? "LinkedIn" : "Naukri", "search-snippet"));
                }
            }
            var added = store.Update(d =>
            {
                var count = 0;
                foreach (var job in found)
                    if (!d.Jobs.Any(j => j.Url.TrimEnd('/') == job.Url.TrimEnd('/'))) { d.Jobs.Add(job); count++; }
                return count;
            });
            return new("complete", plan, added, $"Added {added} indexed listings. Results may be outdated; confirm availability on the original site. Scores use search snippets, not full descriptions. Existing listings are kept without duplicates.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return new("provider-error", plan, 0, "Search provider unavailable or response unsupported. Check connection settings; personalized search links remain available.");
        }
    }
    private static string Plain(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", ""));
}
