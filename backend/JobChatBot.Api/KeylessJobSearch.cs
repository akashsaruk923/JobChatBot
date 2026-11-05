using System.Net;
using System.Text.RegularExpressions;

namespace JobChatBot.Api;

// Public guest listings only. Never submit credentials or attempt to bypass a challenge.
public static class KeylessJobSearch
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    public static async Task<DiscoveryResult> Search(IHttpClientFactory clients, WorkspaceStore store, SearchPlan plan, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?keywords=" + Uri.EscapeDataString(plan.Role) +
                "&location=" + Uri.EscapeDataString(plan.Location) + "&start=0");
            request.Headers.UserAgent.ParseAdd("JobChatBot/1.0");
            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new("provider-error", plan, 0, $"Public LinkedIn search returned HTTP {(int)response.StatusCode}. No API key is required, but access may be restricted or rate limited. Use the search links or import a listing manually; no access checks were bypassed.");
            await response.Content.LoadIntoBufferAsync(2_000_000, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            var jobs = Parse(html);
            if (jobs.Length == 0 && !string.IsNullOrWhiteSpace(html))
                return new("provider-error", plan, 0, "Public search returned no recognized job cards. The page may have changed or require verification. Use the search links or import listings manually.");
            var added = store.Update(d =>
            {
                var count = 0;
                foreach (var job in jobs)
                    if (!d.Jobs.Any(j => j.Url.TrimEnd('/') == job.Url.TrimEnd('/'))) { d.Jobs.Add(job); count++; }
                return count;
            });
            return new("complete", plan, added, $"Added {added} public LinkedIn listings without an API key. Scores use listing-card text, not full descriptions. Naukri search remains available through its search link. Verify availability on the original site.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or RegexMatchTimeoutException)
        {
            return new("provider-error", plan, 0, "Public LinkedIn search is unavailable or its response is unsupported. No API key is needed. Check network access or use search links and manual listing import.");
        }
    }

    public static Job[] Parse(string html)
    {
        var jobs = new List<Job>();
        foreach (System.Text.RegularExpressions.Match card in Regex.Matches(html, @"<li\b[^>]*>(.*?)</li\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout))
        {
            var body = card.Groups[1].Value;
            var link = Regex.Match(body, "href\\s*=\\s*[\"'](https://(?:www\\.)?linkedin\\.com/jobs/view/[^\"']+)[\"']", RegexOptions.IgnoreCase, RegexTimeout);
            if (!Uri.TryCreate(WebUtility.HtmlDecode(link.Groups[1].Value), UriKind.Absolute, out var uri) || !AssistedApplyService.SupportedUrl(uri.AbsoluteUri)) continue;
            var title = Field(body, "base-search-card__title");
            if (title.Length == 0) continue;
            var company = Field(body, "base-search-card__subtitle");
            var location = Field(body, "job-search-card__location");
            var url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (jobs.Any(j => j.Url == url)) continue;
            jobs.Add(new(Guid.NewGuid().ToString("N"), title[..Math.Min(200, title.Length)], company.Length > 0 ? company : "Company not provided — check listing",
                location, url, string.Join(" · ", new[] { title, company, location }.Where(s => s.Length > 0)), DateTimeOffset.UtcNow, "LinkedIn", "public-listing-card"));
            if (jobs.Count == 25) break;
        }
        return jobs.ToArray();
    }
    private static string Field(string html, string className)
    {
        var match = Regex.Match(html, "<(?<tag>h[1-6]|span|div)\\b[^>]*class\\s*=\\s*[\"'][^\"']*\\b" + Regex.Escape(className) + "\\b[^\"']*[\"'][^>]*>(?<value>.*?)</\\k<tag>\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(match.Groups["value"].Value, "<[^>]*>", " ", RegexOptions.None, RegexTimeout)), @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }
}
