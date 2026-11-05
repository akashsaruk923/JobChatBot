using Microsoft.Playwright;
namespace JobChatBot.Api;
public record ProfileUpdateResult(bool Verified, string Detail);
public static class NaukriProfileUpdate
{
    public static async Task<ProfileUpdateResult> Update(IPage page, string name, string resumePath, CancellationToken ct = default)
    {
        if (!File.Exists(resumePath) || new FileInfo(resumePath).Length > 2 * 1024 * 1024 || !new[] { ".pdf", ".docx" }.Contains(Path.GetExtension(resumePath).ToLowerInvariant()))
            return new(false, "Choose an existing PDF/DOCX resume up to 2 MB. Nothing was changed.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return new(false, "A valid, exact profile name is required.");
        if (!OnProfile(page) || await BrowserApplicationForm.NeedsLogin(page)) return new(false, "Open your own Naukri profile and complete login/verification first.");
        // Change only the name field, and only if it differs from the user's exact configured name.
        var displayedName = page.Locator(".name-wrapper .name, .name-wrapper .hdn, .profile-name, [data-testid='profile-name']").Filter(new() { Visible = true });
        try { await displayedName.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 }); }
        catch (System.TimeoutException) { return new(false, "Profile name did not load. Complete login in Edge and retry."); }
        if (await displayedName.CountAsync() != 1) return new(false, "Unsupported name section. Profile update paused; no name or resume changed.");
        if ((await displayedName.InnerTextAsync()).Trim() != name.Trim())
        {
            var edit = page.Locator(".name-wrapper .edit, .name-wrapper .icon-edit, [data-testid='edit-basic-details']").Filter(new() { Visible = true });
            if (await edit.CountAsync() != 1) return new(false, "Cannot identify the name editor. Nothing was changed.");
            await edit.ClickAsync();
            var field = page.Locator("#name, input[name='name'], [data-testid='name-input']").Filter(new() { Visible = true });
            if (await field.CountAsync() != 1) return new(false, "Cannot identify the exact name field. Update paused.");
            await field.FillAsync(name.Trim());
            var dialog = page.Locator("[role=dialog], .lightbox, .drawer").Filter(new() { Has = field });
            if (await dialog.CountAsync() != 1) return new(false, "Cannot isolate the name editor's Save button. No Save clicked.");
            var save = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
            if (await save.CountAsync() != 1 || !await save.IsEnabledAsync()) return new(false, "Name Save button is unavailable. Review the form in Edge.");
            ct.ThrowIfCancellationRequested();
            await save.ClickAsync();
            try { await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10000 }); }
            catch (System.TimeoutException) { return new(false, "Name Save did not complete. Review the browser; no success assumed."); }
        }
        if (!OnProfile(page)) return new(false, "Profile origin changed. Upload cancelled.");
        var upload = page.Locator("input#attachCV[type=file], #lazyAttachCV input[type=file], [data-testid='resume-upload']");
        if (await upload.CountAsync() != 1) return new(false, "Resume upload field is unsupported. Review the visible profile page.");
        // Capture a fresh, visible upload-success signal rather than an old toast.
        var success = page.Locator(".toast-success, .success-msg, .msgBox.success, [role=status]").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("resume.*successfully.*(uploaded|updated)|resume.*(uploaded|updated).*success|successfully.*(uploaded|updated).*resume", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).Filter(new() { Visible = true });
        if (await success.CountAsync() > 0) return new(false, "An old resume success message is still visible. Retry after it clears; no new upload attempted.");
        ct.ThrowIfCancellationRequested();
        await upload.SetInputFilesAsync(resumePath);
        try { await success.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
        catch (System.TimeoutException) { return new(false, "Upload outcome was not confirmed. Check the profile; no automatic upload retry or application follows."); }
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (!OnProfile(page) || await BrowserApplicationForm.NeedsLogin(page)) return new(false, "Re-login required before verifying profile persistence.");
        var verifiedName = page.Locator(".name-wrapper .name, .name-wrapper .hdn, .profile-name, [data-testid='profile-name']").Filter(new() { Visible = true });
        var filename = page.Locator("#lazyAttachCV, [data-testid='resume-section']").Filter(new() { HasText = Path.GetFileName(resumePath) });
        try { await filename.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 }); }
        catch (System.TimeoutException) { return new(false, "Uploaded resume filename was not present after reload. Review the profile in Edge."); }
        if (await verifiedName.CountAsync() != 1 || (await verifiedName.InnerTextAsync()).Trim() != name.Trim() || await filename.CountAsync() != 1)
            return new(false, "Profile reloaded but name/resume filename could not be verified. Review in Edge.");
        return new(true, "Resume upload confirmed; configured name and resume filename verified after profile reload. Unchanged names are verified without inventing edits.");
    }
    public static bool OnProfile(IPage page) => Uri.TryCreate(page.Url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "www.naukri.com" && u.IsDefaultPort && u.UserInfo.Length == 0 && u.AbsolutePath.StartsWith("/mnjuser/profile", StringComparison.Ordinal);
}
