namespace JobChatBot.Api;

public record ResumeProfile(string Name = "", string Email = "", string WhatsApp = "", string ResumeText = "", string DesiredRole = "", string Location = "");
public record JobInput(string Title, string Company, string Location, string Url, string Description);
public record Job(string Id, string Title, string Company, string Location, string Url, string Description, DateTimeOffset AddedAt, string Source = "manual", string DescriptionKind = "full");
public record Match(Job Job, int Score, string[] MatchedSkills, string[] MissingSkills);
public record Application(string Id, string JobId, string Status, DateTimeOffset ConfirmedAt);
public record Notification(string Id, string ApplicationId, string Channel, string Status, string Detail, DateTimeOffset CreatedAt);
public record ChatInput(string Message);
public record ConfirmInput(bool SubmittedByMe);
public class WorkspaceData
{
    public ResumeProfile Profile { get; set; } = new();
    public List<Job> Jobs { get; set; } = [];
    public List<Application> Applications { get; set; } = [];
    public List<Notification> Notifications { get; set; } = [];
}
