namespace JobChatBot.Api;

public record ResumeProfile(string Name = "", string Email = "", string WhatsApp = "", string ResumeText = "", string DesiredRole = "", string Location = "", string FirstName = "", string LastName = "", string PhoneNumber = "");
public record JobInput(string Title, string Company, string Location, string Url, string Description);
public record Job(string Id, string Title, string Company, string Location, string Url, string Description, DateTimeOffset AddedAt, string Source = "manual", string DescriptionKind = "full");
public record Match(Job Job, int Score, string[] MatchedSkills, string[] MissingSkills);
public record Application(string Id, string JobId, string Status, DateTimeOffset ConfirmedAt);
public record Notification(string Id, string ApplicationId, string Channel, string Status, string Detail, DateTimeOffset CreatedAt);
public record ChatInput(string Message);
public record ConfirmInput(bool SubmittedByMe);
public class WorkspaceData
{
    public BotState Bot { get; set; } = new();
    public ResumeAsset? ResumeAsset { get; set; }
    public List<ApplicationAttempt> ApplicationAttempts { get; set; } = [];
    public ResumeProfile Profile { get; set; } = new();
    public List<Job> Jobs { get; set; } = [];
    public List<Application> Applications { get; set; } = [];
    public List<Notification> Notifications { get; set; } = [];
}

public record ResumeAsset(string StoredName, string OriginalName, string TextHash);
public record ApplicationAttempt(string Id, string JobId, string Status, string Detail, string ProfileHash, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ReviewToken = null, ReviewField[]? Fields = null, string? LoginIdentityHash = null);
public record ReviewField(string Label, string Value);
public record SubmitApplicationInput(bool Approved, string ReviewToken);

public record BotPolicy(bool AuthorizeSubmission, int MinimumMatchScore = 70, int MaxApplicationsPerRun = 3, int IntervalMinutes = 60, bool AllowNaukriProfileApply = false);
public record BotState(bool Enabled = false, string Status = "stopped", string Detail = "Bot has not been started.", BotPolicy? Policy = null, string? ProfileHash = null, DateTimeOffset? NextRunAt = null, DateTimeOffset? LastRunAt = null, int SubmittedThisRun = 0, string? AuthorizationId = null);

public record ResolveUnknownInput(bool CheckedOriginalSite);
