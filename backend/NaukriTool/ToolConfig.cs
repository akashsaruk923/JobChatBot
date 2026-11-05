using System.Net.Mail;
using System.Text.Json;
using JobChatBot.Api;
internal record ToolConfig(string Name, string Email, string ResumePath, string Role, string Location, double ExperienceYears, int MaxApplicationsPerCycle = 3, int MinimumMatchScore = 50)
{
    public ResumeProfile Profile()
    {
        Validate();
        return new(Name:Name, Email:Email, ResumeText:ResumeReader.Extract(File.ReadAllBytes(ResumePath), ResumePath), DesiredRole:Role, Location:Location, ExperienceYears:ExperienceYears);
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || !MailAddress.TryCreate(Email, out _) || string.IsNullOrWhiteSpace(Role) || Role.Length > 200 || Location.Length > 200)
            throw new InvalidDataException("Provide exact name, valid email, role and location.");
        if (!File.Exists(ResumePath) || new FileInfo(ResumePath).Length > 2*1024*1024 || !new[] { ".pdf", ".docx" }.Contains(Path.GetExtension(ResumePath).ToLowerInvariant()))
            throw new InvalidDataException("Resume must be an existing PDF/DOCX up to 2 MB.");
        if (!double.IsFinite(ExperienceYears) || ExperienceYears < 0 || ExperienceYears > 60 || MaxApplicationsPerCycle is < 1 or > 10 || MinimumMatchScore is < 0 or > 100)
            throw new InvalidDataException("Experience must be 0-60, applications per cycle 1-10, and match score 0-100.");
    }
    public static ToolConfig LoadOrCreate(string path)
    {
        if (File.Exists(path)) return JsonSerializer.Deserialize<ToolConfig>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid tool config.");
        string Ask(string message) { Console.Write(message + ": "); return Console.ReadLine()?.Trim().Trim('"') ?? throw new OperationCanceledException(); }
        Console.WriteLine("One-time setup. Local configuration contains no API keys or passwords.");
        var name = Ask("Exact Naukri profile name"); var email = Ask("Your Naukri account email");
        var resume = Path.GetFullPath(Ask("Full path to your PDF/DOCX resume"));
        var role = Ask("Target job role"); var location = Ask("Location");
        if (!double.TryParse(Ask("Total experience in years (e.g. 4)"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var experience)) throw new InvalidDataException("Invalid experience.");
        var config = new ToolConfig(name,email,resume,role,location,experience);
        _ = config.Profile(); // Validate extraction before retaining first-run configuration.
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented=true }));
        return config;
    }
}
