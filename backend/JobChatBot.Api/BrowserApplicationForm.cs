using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
namespace JobChatBot.Api;

public record BrowserFormResult(string Status, string Detail, string? ReviewToken = null, ReviewField[]? Fields = null);

// Conservative English-language LinkedIn Easy Apply adapter. Unknown questions remain for the person.
public static class BrowserApplicationForm
{
    private static ILocator Dialog(IPage page) => page.Locator("[role=dialog], .artdeco-modal").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Next|Review|Submit application", RegexOptions.IgnoreCase) }) }).First;
    private static ILocator Submit(ILocator dialog) => dialog.GetByRole(AriaRole.Button, new() { Name = "Submit application", Exact = true });
    public static async Task<BrowserFormResult> Prepare(IPage page, ResumeProfile profile, string? resumePath, string? originalName = null)
    {
        if (await NeedsLogin(page)) return new("needs-user-action", "Complete login, verification or CAPTCHA yourself in the Edge window, then click Continue. No challenge is bypassed.");
        var dialog = Dialog(page);
        if (!await dialog.IsVisibleAsync())
        {
            var easyApply = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Easy Apply(?:\\s|$)", RegexOptions.IgnoreCase) });
            if (await easyApply.CountAsync() != 1 || !await easyApply.IsVisibleAsync())
                return new("needs-user-action", "Open the Easy Apply form in Edge. External employer forms and unknown layouts require manual completion.");
            await easyApply.ClickAsync(new() { Timeout = 5000 });
        }
        for (var step = 0; step < 8; step++)
        {
            if (await NeedsLogin(page)) return new("needs-user-action", "Complete login or verification manually in Edge, then Continue.");
            dialog = Dialog(page);
            if (!await dialog.IsVisibleAsync()) return new("needs-user-action", "No supported application dialog is visible. Complete the current screen in Edge, then Continue.");
            await Fill(dialog, "^Full name$", profile.Name);
            await Fill(dialog, "^First name$", profile.FirstName);
            await Fill(dialog, "^Last name$", profile.LastName);
            var phone = profile.PhoneNumber;
            var country = dialog.GetByLabel(new Regex("^(?:(?:Phone|Mobile phone) country code.*|Country code)$", RegexOptions.IgnoreCase));
            if (phone.Length > 0 && await country.CountAsync() == 1)
            {
                var selected = await country.EvaluateAsync<string>("e => e.tagName === 'SELECT' ? (e.selectedOptions[0]?.textContent || e.value) : (e.value || e.textContent || '')");
                var prefix = Regex.Match(selected, @"\+\d{1,3}").Value;
                if (prefix.Length == 0 || !phone.StartsWith(prefix, StringComparison.Ordinal))
                    return new("needs-user-action", "Choose the correct phone country code manually in Edge. The bot will not submit a phone number under a mismatched country code.");
                phone = phone[prefix.Length..];
            }
            await Fill(dialog, "^(Phone|Phone number|Mobile phone number)$", phone);
            await Fill(dialog, "^Email(?: address)?$", profile.Email);
            // WhatsApp may differ from an application phone number. Do not infer one from the other.
            var upload = dialog.Locator("input[type=file]");
            if (resumePath is not null && await upload.CountAsync() == 1)
            {
                var accept = (await upload.GetAttributeAsync("accept")) ?? "";
                var extension = Path.GetExtension(resumePath);
                if (accept.Length == 0 || accept.Contains(extension, StringComparison.OrdinalIgnoreCase) || accept.Contains("application/pdf") && extension == ".pdf")
                    await upload.SetInputFilesAsync(new FilePayload { Name = Path.GetFileName(originalName ?? resumePath), MimeType = extension == ".pdf" ? "application/pdf" : extension == ".docx" ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document" : "text/plain", Buffer = await File.ReadAllBytesAsync(resumePath) }, new() { Timeout = 5000 });
            }
            var invalid = await dialog.EvaluateAsync<string[]>("""root => [...root.querySelectorAll('input,select,textarea')].filter(e => (e.getClientRects().length || e.type === 'file') && (e.required && !e.checkValidity() || e.getAttribute('aria-required') === 'true' && !e.value)).map(e => e.labels?.[0]?.textContent?.trim() || e.getAttribute('aria-label') || e.name || 'Required field')""");
            if (invalid.Length > 0) return new("needs-user-action", "Complete required fields manually in Edge: " + string.Join(", ", invalid.Take(10)) + ". We do not guess answers or accept consent for you.");
            var submit = Submit(dialog);
            if (await submit.CountAsync() == 1 && await submit.IsVisibleAsync())
            {
                if (!await submit.IsEnabledAsync()) return new("needs-user-action", "Submit is disabled. Complete the current validation/resume requirements manually in Edge.");
                var fields = await Snapshot(dialog);
                return new("ready-to-submit", "Review every answer and the attached resume in Edge. Approval submits this application once. No application has been sent yet.", Hash(fields), fields);
            }
            var next = dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Next|Review)$", RegexOptions.IgnoreCase) });
            if (await next.CountAsync() != 1 || !await next.IsEnabledAsync()) return new("needs-user-action", "Review this screen manually in Edge. No unrecognized button was clicked.");
            var before = await dialog.InnerHTMLAsync();
            await next.ClickAsync(new() { Timeout = 5000 });
            // Wait for actual step change instead of repeatedly clicking the same form.
            try { await page.WaitForFunctionAsync("previous => { const e = document.querySelector('[role=dialog],.artdeco-modal'); return !e || e.innerHTML !== previous; }", before, new() { Timeout = 4000 }); }
            catch (System.TimeoutException) { return new("needs-user-action", "The form did not advance. Review validation messages in Edge before continuing."); }
        }
        return new("needs-user-action", "Reached the step limit. Continue the form manually in Edge, then review again.");
    }
    public static async Task<bool> SubmitReviewed(IPage page, string token)
    {
        if (await NeedsLogin(page)) return false;
        var dialog = Dialog(page);
        if (!await dialog.IsVisibleAsync() || Hash(await Snapshot(dialog)) != token) return false;
        var submit = Submit(dialog);
        if (await submit.CountAsync() != 1 || !await submit.IsVisibleAsync() || !await submit.IsEnabledAsync()) return false;
        await submit.ClickAsync(new() { Timeout = 5000 });
        return true;
    }
    public static async Task<bool> HasReceipt(IPage page)
    {
        var receipt = page.Locator("[role=dialog],.artdeco-modal").GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^(Application sent|Your application was sent.*)$", RegexOptions.IgnoreCase) });
        return await receipt.CountAsync() == 1 && await receipt.IsVisibleAsync();
    }
    private static async Task Fill(ILocator dialog, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var field = dialog.GetByLabel(new Regex(label.TrimEnd('$') + @"\s*\*?$", RegexOptions.IgnoreCase));
        if (await field.CountAsync() == 1 && await field.IsVisibleAsync() && await field.IsEditableAsync() && string.IsNullOrWhiteSpace(await field.InputValueAsync()))
            await field.FillAsync(value, new() { Timeout = 3000 });
    }
    public static async Task<bool> NeedsLogin(IPage page)
    {
        if (Regex.IsMatch(page.Url, "checkpoint|/challenge|/login|/authwall", RegexOptions.IgnoreCase)) return true;
        var gate = page.Locator("input[type=password]:visible, iframe[src*='captcha']:visible, input[name=captcha]:visible");
        return await gate.CountAsync() > 0;
    }
    private static async Task<ReviewField[]> Snapshot(ILocator dialog)
    {
        var json = await dialog.EvaluateAsync<string>("""root => JSON.stringify([...root.querySelectorAll('input,select,textarea')].filter(e => e.type !== 'password' && e.type !== 'hidden' && (e.getClientRects().length || e.type === 'file')).map(e => ({label:e.labels?.[0]?.textContent?.trim() || e.getAttribute('aria-label') || e.name || e.type,value:e.type === 'file' ? [...e.files].map(f=>f.name+' ('+f.size+' bytes)').join(', ') : ['checkbox','radio'].includes(e.type) ? String(e.checked) : e.value})))""");
        return JsonSerializer.Deserialize<ReviewField[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }
    private static string Hash(ReviewField[] fields) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))));
}
