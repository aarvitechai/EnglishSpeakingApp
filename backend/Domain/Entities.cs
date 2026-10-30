using Microsoft.Data.SqlTypes;

namespace Domain;

public static class Roles
{
    public const string Candidate = "candidate";
    public const string OrgAdmin = "org_admin";
    public const string Admin = "admin";
    public const string SuperAdmin = "super_admin";
}

public static class Plans
{
    public const string Free = "free";
    public const string Plus = "plus";
    public const string Pro = "pro";
    public static readonly string[] All = [Free, Plus, Pro];
    public static int Rank(string plan) => Array.IndexOf(All, plan);
}

public static class Categories
{
    public const string Hr = "hr";
    public const string Behavioural = "behavioural";
    public const string Technical = "technical";
    public static readonly string[] All = [Hr, Behavioural, Technical];
}

public record LevelInfo(string Id, string Name, string Summary, int Minutes, string MinPlan, string Persona);

/// <summary>The five interview levels, from no experience at all to expert. Stored in Difficulty as "1" to "5".</summary>
public static class Levels
{
    public static readonly LevelInfo[] All =
    [
        new("1", "Zero", "Never given an interview? Start here, with simple questions and a hint for each one.", 10, Plans.Free,
            "Very patient, kind mentor for a complete beginner with no interview experience. Uses short sentences and simple words, and encourages the candidate after every answer."),
        new("2", "Beginner", "The common questions every fresher gets, with a friendly interviewer.", 10, Plans.Free,
            "Warm and encouraging campus recruiter. Puts the candidate at ease and keeps follow-ups gentle."),
        new("3", "Intermediate", "A realistic round that asks for specifics when answers are vague.", 15, Plans.Plus,
            "Professional, neutral hiring manager. Polite, and asks for specifics when answers are vague."),
        new("4", "Advanced", "Tough questions and probing follow-ups, like a final round.", 20, Plans.Plus,
            "Demanding senior interviewer. Respectful but probes assumptions and challenges weak reasoning."),
        new("5", "Expert", "A panel-level grilling on judgement, trade-offs and depth.", 25, Plans.Pro,
            "Senior panel chair at a top employer. Courteous but relentless: tests every claim, raises counter-arguments and expects structured, nuanced answers."),
    ];

    public static LevelInfo? Find(string id) => All.FirstOrDefault(l => l.Id == id);

    public static string Label(string id) => Find(id) is { } l ? $"Level {l.Id} of 5 ({l.Name})" : id;
}

public static class SessionStatus
{
    public const string Created = "created";
    public const string Active = "active";
    public const string Evaluating = "evaluating";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Abandoned = "abandoned";
}

public static class SessionModes
{
    public const string Text = "text";
    public const string Voice = "voice";
}

public static class Speakers
{
    public const string Interviewer = "interviewer";
    public const string Candidate = "candidate";
}

public static class PaymentStatus
{
    public const string Created = "created";
    public const string Captured = "captured";
}

public static class SubscriptionStatus
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Cancelled = "cancelled";
}

public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Email { get; set; }
    public required string Name { get; set; }
    public string Role { get; set; } = Roles.Candidate;
    public Guid? OrgId { get; set; }
    public Organization? Org { get; set; }
    public string Plan { get; set; } = Plans.Free;
    public string? PasswordHash { get; set; }
    public string? GoogleSubject { get; set; }
    public DateTime TermsAcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool IsDisabled { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    /// <summary>Changing this signs the user out everywhere: done on password, role and status changes.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public Profile? Profile { get; set; }
}

public class Organization
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Type { get; set; }
    public int Seats { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Profile
{
    public Guid UserId { get; set; }
    public string? ResumeBlobUrl { get; set; }
    public string? ParsedResumeJson { get; set; }
    public string? TargetRole { get; set; }
    public string? ExperienceLevel { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class Rubric
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Category { get; set; }
    public int Version { get; set; }
    public required string DimensionsJson { get; set; }
}

public class InterviewTemplate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Category { get; set; }
    public required string Difficulty { get; set; }
    public required string Persona { get; set; }
    public Guid RubricId { get; set; }
    public Rubric? Rubric { get; set; }
    public int DurationMin { get; set; }
}

public class Question
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Category { get; set; }
    public required string Difficulty { get; set; }
    public required string Text { get; set; }
    public string Tags { get; set; } = "";
    /// <summary>A sentence starter shown to complete beginners at level 1.</summary>
    public string? Hint { get; set; }
    public SqlVector<float>? Embedding { get; set; }
}

public class Session
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid TemplateId { get; set; }
    public InterviewTemplate? Template { get; set; }
    public string? JobDescription { get; set; }
    public string Mode { get; set; } = SessionModes.Text;
    public string Status { get; set; } = SessionStatus.Created;
    /// <summary>JSON array of the question texts chosen when the session was created.</summary>
    public string PlanJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime? VoiceConsentAt { get; set; }
    public long CostPaise { get; set; }
    public string? FailureReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<Turn> Turns { get; set; } = [];
}

public class Turn
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public int Seq { get; set; }
    public required string Speaker { get; set; }
    public required string Text { get; set; }
    /// <summary>Milliseconds since session start. Only meaningful for voice answers.</summary>
    public int? StartMs { get; set; }
    public int? EndMs { get; set; }
    public int LongPauses { get; set; }
}

public class Evaluation
{
    public Guid SessionId { get; set; }
    public int RubricVersion { get; set; }
    public int OverallScore { get; set; }
    public required string ScoresJson { get; set; }
    public required string FeedbackJson { get; set; }
    public required string ModelVersion { get; set; }
    public int? HelpfulRating { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DeliveryMetric
{
    public Guid SessionId { get; set; }
    public int? Wpm { get; set; }
    public int FillerCount { get; set; }
    public int LongPauses { get; set; }
    public double TalkRatio { get; set; }
    public double? AvgAnswerSec { get; set; }
    public double AvgAnswerWords { get; set; }
}

public class PracticePlan
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid? SourceSessionId { get; set; }
    public required string Skill { get; set; }
    public required string DrillsJson { get; set; }
    public string Status { get; set; } = "open";
    public DateTime DueDate { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class Subscription
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? UserId { get; set; }
    public Guid? OrgId { get; set; }
    public string Plan { get; set; } = Plans.Pro;
    public string Status { get; set; } = SubscriptionStatus.Pending;
    public DateTime? RenewsAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}

public class Payment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    public long AmountPaise { get; set; }
    public string Provider { get; set; } = "razorpay";
    public required string ProviderRef { get; set; }
    public string? ProviderPaymentId { get; set; }
    public string Status { get; set; } = PaymentStatus.Created;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Who did what in the admin area. Deliberately has no foreign keys, so it outlives the accounts it names.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid ActorId { get; set; }
    public required string ActorEmail { get; set; }
    public required string Action { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? TargetEmail { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
