using System.Text.Json;
using System.Text.RegularExpressions;
using Domain;
using Hangfire;
using Infrastructure;
using Infrastructure.Ai;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Api;

public class QuotaExceededException(string message) : Exception(message);

public partial class InterviewService(
    AppDbContext db,
    IInterviewerAi interviewer,
    ScriptedInterviewer fallback,
    AiOptions options,
    IConfiguration config,
    ILogger<InterviewService> logger)
{
    public const int MaxAnswerChars = 4000;
    public const int MaxJobDescriptionChars = 8000;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"[a-z0-9+#.]{2,}")]
    private static partial Regex Token();

    public async Task<string> EffectivePlanAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var active = await db.Subscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active && s.RenewsAt > now)
            .Select(s => s.Plan).ToListAsync(ct);
        return active.OrderByDescending(Plans.Rank).FirstOrDefault() ?? Plans.Free;
    }

    public int MonthlyLimit(string plan) => plan switch
    {
        Plans.Pro => config.GetValue("Quotas:ProPerMonth", 100),
        Plans.Plus => config.GetValue("Quotas:PlusPerMonth", 30),
        _ => config.GetValue("Quotas:FreePerMonth", 5),
    };

    public static bool PlanAllowsLevel(string plan, string level) =>
        Levels.Find(level) is { } l && Plans.Rank(plan) >= Plans.Rank(l.MinPlan);

    public async Task<int> UsedThisMonthAsync(Guid userId, CancellationToken ct = default)
    {
        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await db.Sessions.CountAsync(s => s.UserId == userId && s.CreatedAt >= start, ct);
    }

    public async Task<Session> CreateAsync(Guid userId, InterviewTemplate template, string? jobDescription, string mode, bool voiceConsent, CancellationToken ct)
    {
        var plan = await EffectivePlanAsync(userId, ct);
        if (!PlanAllowsLevel(plan, template.Difficulty))
            throw new QuotaExceededException($"Level {template.Difficulty} is not included in your plan. Please upgrade to unlock it.");
        if (await UsedThisMonthAsync(userId, ct) >= MonthlyLimit(plan))
            throw new QuotaExceededException($"You have used all {MonthLimitLabel(plan)} interviews for this month.");

        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        var session = new Session
        {
            UserId = userId,
            TemplateId = template.Id,
            JobDescription = string.IsNullOrWhiteSpace(jobDescription) ? null : jobDescription.Trim(),
            Mode = mode,
            VoiceConsentAt = voiceConsent ? DateTime.UtcNow : null,
            PlanJson = JsonSerializer.Serialize(await PickQuestionsAsync(userId, template, profile, jobDescription, ct), Web),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;

        string MonthLimitLabel(string p) => MonthlyLimit(p).ToString();
    }

    /// <summary>
    /// Picks questions that overlap the resume, JD and target role, avoiding the user's recent questions.
    /// Lexical matching for now; Questions.Embedding is ready for VECTOR_DISTANCE once an embedding provider is chosen.
    /// </summary>
    private async Task<List<string>> PickQuestionsAsync(Guid userId, InterviewTemplate t, Profile? profile, string? jd, CancellationToken ct)
    {
        int count = Math.Clamp(t.DurationMin / 3, 3, 7);
        var pool = await db.Questions.AsNoTracking()
            .Where(q => q.Category == t.Category && q.Difficulty == t.Difficulty)
            .Select(q => new { q.Text, q.Tags })
            .ToListAsync(ct);

        var recentPlans = await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == userId).OrderByDescending(s => s.CreatedAt).Take(3)
            .Select(s => s.PlanJson).ToListAsync(ct);
        var recent = recentPlans.SelectMany(p => JsonSerializer.Deserialize<List<string>>(p, Web) ?? []).ToHashSet();

        var context = $"{profile?.TargetRole} {profile?.ParsedResumeJson} {jd}".ToLowerInvariant();
        var tokens = Token().Matches(context).Select(m => m.Value).ToHashSet();

        var ranked = pool
            .Select(q => new
            {
                q.Text,
                Score = q.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Count(tag => context.Contains(tag))
                    + Token().Matches(q.Text.ToLowerInvariant()).Count(m => m.Length > 4 && tokens.Contains(m.Value)) * 0.2
                    + Random.Shared.NextDouble() * 0.9
                    - (recent.Contains(q.Text) ? 5 : 0),
            })
            .OrderByDescending(q => q.Score)
            .Take(count)
            .Select(q => q.Text)
            .ToList();

        // A warm start for HR interviews, which nearly always open with an introduction.
        const string opener = "Tell me about yourself.";
        if (t is { Category: Categories.Hr, Difficulty: "2" } && !recent.Contains(opener))
        {
            ranked.Remove(opener);
            ranked.Insert(0, opener);
            if (ranked.Count > count) ranked.RemoveAt(ranked.Count - 1);
        }
        return ranked;
    }

    /// <summary>
    /// Generates and stores the interviewer's next turn. The model call deliberately ignores client disconnects:
    /// if a candidate's connection drops, the turn is still saved and they can resume from where they were.
    /// </summary>
    public async Task<(bool Ended, string Text)> NextInterviewerTurnAsync(Session session, Func<string, Task> onDelta)
    {
        var turns = session.Turns.OrderBy(t => t.Seq).ToList();
        var context = await BuildContextAsync(session, turns);
        var usage = new AiUsage();
        var text = new System.Text.StringBuilder();

        async Task Send(string delta)
        {
            text.Append(delta);
            try { await onDelta(delta); }
            catch (Exception e) when (e is OperationCanceledException or IOException) { /* client left; keep generating so the turn is saved */ }
        }

        try
        {
            await foreach (var delta in interviewer.StreamAsync(context, usage, CancellationToken.None)) await Send(delta);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Interviewer model failed for session {SessionId}", session.Id);
            if (text.Length > 0)
                await Send(" Let's move on.");
            else
                await foreach (var delta in fallback.StreamAsync(context, usage, CancellationToken.None)) await Send(delta);
        }

        session.Turns.Add(new Turn
        {
            SessionId = session.Id,
            Seq = turns.Count == 0 ? 1 : turns[^1].Seq + 1,
            Speaker = Speakers.Interviewer,
            Text = text.ToString().Trim(),
        });
        session.CostPaise += usage.CostPaise(options.UsdToInr);
        await db.SaveChangesAsync();

        bool isClosing = context.IsClosing || text.ToString().StartsWith("Thank you, that's all", StringComparison.Ordinal);
        return (isClosing, text.ToString());
    }

    private async Task<InterviewContext> BuildContextAsync(Session session, List<Turn> turns)
    {
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == session.UserId);
        string? resume = null;
        if (profile?.ParsedResumeJson is not null)
        {
            using var doc = JsonDocument.Parse(profile.ParsedResumeJson);
            resume = doc.RootElement.GetProperty("text").GetString();
        }
        var plan = JsonSerializer.Deserialize<List<string>>(session.PlanJson, Web) ?? [];
        return new InterviewContext(
            session.Template!.Category,
            session.Template.Difficulty,
            session.Template.Persona,
            plan,
            profile?.TargetRole,
            resume,
            session.JobDescription,
            turns.Select(t => new HistoryItem(t.Speaker, t.Text)).ToList(),
            turns.Count(t => t.Speaker == Speakers.Candidate),
            // Room for one follow-up on most questions, and a hard ceiling on cost per interview.
            plan.Count + plan.Count / 2 + 1);
    }

    /// <summary>Moves a session out of active. Sessions with no answers cost nothing to evaluate, so they are abandoned.</summary>
    public async Task EndAsync(Session session, IBackgroundJobClient jobs)
    {
        session.EndedAt ??= DateTime.UtcNow;
        bool hasAnswers = session.Turns.Any(t => t.Speaker == Speakers.Candidate);
        session.Status = hasAnswers ? SessionStatus.Evaluating : SessionStatus.Abandoned;
        await db.SaveChangesAsync();
        if (hasAnswers) jobs.Enqueue<EvaluateSessionJob>(j => j.RunAsync(session.Id));
    }
}

public class EvaluateSessionJob(AppDbContext db, EvaluationService evaluation, IHubContext<ReportsHub> hub, ILogger<EvaluateSessionJob> logger)
{
    private const int MaxAttempts = 3;

    // Retries are local so we can mark the session failed, and tell the user, once they run out.
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid sessionId)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await evaluation.RunAsync(sessionId, CancellationToken.None);
                break;
            }
            catch (Exception e) when (attempt < MaxAttempts)
            {
                logger.LogWarning(e, "Evaluation attempt {Attempt} failed for session {SessionId}", attempt, sessionId);
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
            catch (Exception e)
            {
                logger.LogError(e, "Evaluation failed for session {SessionId}", sessionId);
                db.ChangeTracker.Clear();
                var failed = await db.Sessions.FirstAsync(s => s.Id == sessionId);
                failed.Status = SessionStatus.Failed;
                failed.FailureReason = "We could not finish scoring this interview.";
                await db.SaveChangesAsync();
                break;
            }
        }

        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == sessionId);
        await hub.Clients.User(session.UserId.ToString()).SendAsync("reportReady", new { sessionId, status = session.Status });
    }
}
