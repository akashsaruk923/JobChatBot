using System.Net;
using System.Text.RegularExpressions;
namespace JobChatBot.Api;
public static class HrContactDiscovery
{
    public static HrContact[] Extract(Job job, string html, double years)
    {
        if (string.IsNullOrWhiteSpace(job.Company) || job.Company.StartsWith("Company not provided", StringComparison.OrdinalIgnoreCase)) return [];
        // Read only the public job description, never scripts, tracking payloads or recruiter profiles.
        var description = Regex.Match(html, "<div\\b[^>]*class=[\"'][^\"']*show-more-less-html__markup[^\"']*[\"'][^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        if (!description.Success) return [];
        var markup = Regex.Replace(description.Groups[1].Value, @"<(script|style)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var text = WebUtility.HtmlDecode(Regex.Replace(markup, "<[^>]*>", " "));
        var ranges = Regex.Matches(text, @"\b\d{1,2}(?:\.\d)?\s*(?:-|–|to)\s*\d{1,2}(?:\.\d)?\s*(?:years?|yrs?)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        // Multiple conflicting ranges cannot safely establish candidate eligibility.
        if (ranges.Count != 1) return [];
        var range = Experience.Range(ranges[0].Value);
        if (!range.HasValue || !Experience.Fits(years, range.Value.Min, range.Value.Max)) return [];
        return Regex.Matches(text, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(email => !email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase))
            .Select(email => new HrContact(Guid.NewGuid().ToString("N"), job.Company, email, job.Title, job.Url, ranges[0].Value, range.Value.Min, range.Value.Max, DateTimeOffset.UtcNow)).ToArray();
    }
    public static async Task<object> Search(WorkspaceStore store, JobDiscovery discovery, IHttpClientFactory clients, CancellationToken ct)
    {
        var profile = store.Read(d => d.Profile);
        if (!profile.ExperienceYears.HasValue || string.IsNullOrWhiteSpace(profile.ResumeText)) return new { status = "needs-profile", added = 0, message = "Save resume, target role and total experience first." };
        var plan = discovery.Plan(profile);
        var client = clients.CreateClient(); client.Timeout = TimeSpan.FromSeconds(30);
        var contacts = new List<HrContact>(); var inspected = 0; var unavailable = 0;
        try
        {
            using var search = await client.GetAsync("https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?keywords=" + Uri.EscapeDataString(plan.Role) + "&location=" + Uri.EscapeDataString(plan.Location) + "&start=0", ct);
            if (!search.IsSuccessStatusCode) return new { status = "provider-error", added = 0, message = $"LinkedIn public search returned HTTP {(int)search.StatusCode}. No contacts were invented." };
            await search.Content.LoadIntoBufferAsync(2_000_000, ct);
            var jobs = KeylessJobSearch.Parse(await search.Content.ReadAsStringAsync(ct));
            if (jobs.Length == 0) return new { status = "provider-error", added = 0, message = "No recognized public LinkedIn listings. Access or page format may have changed." };
            foreach (var job in jobs.Take(10))
            {
                ct.ThrowIfCancellationRequested();
                var id = Regex.Match(new Uri(job.Url).AbsolutePath, @"(?:-|/)(\d+)/?$", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value;
                if (id.Length == 0) { unavailable++; continue; }
                using var response = await client.GetAsync("https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/" + id, ct);
                if (!response.IsSuccessStatusCode) { unavailable++; continue; }
                await response.Content.LoadIntoBufferAsync(2_000_000, ct);
                inspected++;
                contacts.AddRange(Extract(job, await response.Content.ReadAsStringAsync(ct), profile.ExperienceYears.Value));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RegexMatchTimeoutException) { return new { status = "provider-error", added = 0, message = "Public LinkedIn lookup failed. No partial contacts were saved; retry later. No emails were guessed." }; }
        var added = store.Update(d => { var count = 0; foreach (var contact in contacts) if (!d.HrContacts.Any(c => c.SourceUrl == contact.SourceUrl && c.Email.Equals(contact.Email, StringComparison.OrdinalIgnoreCase))) { d.HrContacts.Add(contact); count++; } return count; });
        return new { status = "complete", added, message = $"Checked {inspected} public job descriptions; added {added} published contact emails matching {profile.ExperienceYears} years. {unavailable} descriptions unavailable. Emails are published contacts, not verified HR identities; no email was sent. Missing/ambiguous experience ranges are excluded." };
    }
}
