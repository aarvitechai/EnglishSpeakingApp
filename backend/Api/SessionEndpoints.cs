using System.Security.Claims;
using System.Text.Json;
using Domain;
using Hangfire;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api;

public record CreateSessionRequest(string Category, string Level, string? JobDescription, string? Mode, bool VoiceConsent);
public record MessageRequest(string Text, int? StartMs, int? EndMs, int? LongPauses);
public record FeedbackRatingRequest(int Rating);

public static class SessionEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/templates", async (AppDbContext db) =>
            Results.Ok(await db.InterviewTemplates.AsNoTracking()
                .Select(t => new { t.Id, t.Category, level = t.Difficulty, t.DurationMin }).ToListAsync()));

        var g = app.MapGroup("/api/sessions").RequireAuthorization();

        g.MapPost("/", async (CreateSessionRequest r, ClaimsPrincipal p, AppDbContext db, InterviewService service, CancellationToken ct) =>
        {
            var template = await db.InterviewTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Category == r.Category && t.Difficulty == r.Level, ct);
            if (template is null) return Http.Problem(400, "Please choose a valid round and level.");
            if (r.JobDescription?.Length > InterviewService.MaxJobDescriptionChars) return Http.Problem(400, "Job description is too long (8,000 characters at most).");
            var mode = r.Mode == SessionModes.Voice ? SessionModes.Voice : SessionModes.Text;
            if (mode == SessionModes.Voice && !r.VoiceConsent) return Http.Problem(400, "Please agree to microphone use to start a voice interview.");

            try
            {
                var session = await service.CreateAsync(p.UserId(), template, r.JobDescription, mode, r.VoiceConsent, ct);
                return Results.Created($"/api/sessions/{session.Id}", new { session.Id });
            }
            catch (QuotaExceededException e)
            {
                return Http.Problem(402, e.Message);
            }
        }).RequireRateLimiting("ai");

        g.MapGet("/", async (ClaimsPrincipal p, AppDbContext db) =>
        {
            var id = p.UserId();
            var rows = await (
                from s in db.Sessions.AsNoTracking()
                where s.UserId == id
                join e in db.Evaluations on s.Id equals e.SessionId into evals
                from e in evals.DefaultIfEmpty()
                orderby s.CreatedAt descending
                select new { s.Id, s.Status, s.Mode, s.CreatedAt, s.Template!.Category, level = s.Template.Difficulty, score = (int?)e.OverallScore })
                .Take(100).ToListAsync();
            return Results.Ok(rows);
        });

        g.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
        {
            var s = await LoadAsync(db, id, p.UserId());
            if (s is null) return Results.NotFound();
            // Level 1 candidates get a sentence starter with each question.
            var hints = new Dictionary<string, string>();
            if (s.Template?.Difficulty == "1")
            {
                var plan = JsonSerializer.Deserialize<List<string>>(s.PlanJson, Web) ?? [];
                hints = await db.Questions.AsNoTracking()
                    .Where(q => q.Hint != null && plan.Contains(q.Text))
                    .ToDictionaryAsync(q => q.Text, q => q.Hint!);
            }
            return Results.Ok(new { session = SessionDto(s), hints });
        });

        g.MapPost("/{id:guid}/start", async (Guid id, ClaimsPrincipal p, AppDbContext db, InterviewService service, HttpContext ctx) =>
        {
            var s = await LoadAsync(db, id, p.UserId());
            if (s is null) { ctx.Response.StatusCode = 404; return; }
            if (s.Status != SessionStatus.Created || s.Turns.Count > 0) { ctx.Response.StatusCode = 409; return; }
            s.Status = SessionStatus.Active;
            s.StartedAt = DateTime.UtcNow;
            await StreamTurnAsync(ctx, service, s);
        }).RequireRateLimiting("ai");

        g.MapPost("/{id:guid}/messages", async (Guid id, MessageRequest r, ClaimsPrincipal p, AppDbContext db, InterviewService service, IBackgroundJobClient jobs, HttpContext ctx) =>
        {
            var s = await LoadAsync(db, id, p.UserId());
            if (s is null) { ctx.Response.StatusCode = 404; return; }
            var text = r.Text?.Trim() ?? "";
            if (text.Length is 0 or > InterviewService.MaxAnswerChars) { ctx.Response.StatusCode = 400; return; }
            var ordered = s.Turns.OrderBy(t => t.Seq).ToList();
            // Turns must alternate. This also rejects the double-submits a flaky connection produces.
            if (s.Status != SessionStatus.Active || ordered.Count == 0 || ordered[^1].Speaker != Speakers.Interviewer) { ctx.Response.StatusCode = 409; return; }

            bool timed = r is { StartMs: >= 0, EndMs: >= 0 } && r.EndMs > r.StartMs;
            s.Turns.Add(new Turn
            {
                SessionId = s.Id,
                Seq = ordered[^1].Seq + 1,
                Speaker = Speakers.Candidate,
                Text = text,
                StartMs = timed ? r.StartMs : null,
                EndMs = timed ? r.EndMs : null,
                LongPauses = Math.Clamp(r.LongPauses ?? 0, 0, 1000),
            });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                ctx.Response.StatusCode = 409;
                return;
            }

            var ended = await StreamTurnAsync(ctx, service, s);
            if (ended) await service.EndAsync(s, jobs);
        }).RequireRateLimiting("ai");

        g.MapPost("/{id:guid}/end", async (Guid id, ClaimsPrincipal p, AppDbContext db, InterviewService service, IBackgroundJobClient jobs) =>
        {
            var s = await LoadAsync(db, id, p.UserId());
            if (s is null) return Results.NotFound();
            if (s.Status is SessionStatus.Created or SessionStatus.Active) await service.EndAsync(s, jobs);
            return Results.Ok(new { s.Status });
        });

        g.MapPost("/{id:guid}/retry-evaluation", async (Guid id, ClaimsPrincipal p, AppDbContext db, IBackgroundJobClient jobs) =>
        {
            var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId());
            if (s is null) return Results.NotFound();
            if (s.Status != SessionStatus.Failed) return Results.Conflict();
            s.Status = SessionStatus.Evaluating;
            await db.SaveChangesAsync();
            jobs.Enqueue<EvaluateSessionJob>(j => j.RunAsync(id));
            return Results.Ok(new { s.Status });
        }).RequireRateLimiting("ai");

        g.MapGet("/{id:guid}/report", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
        {
            var s = await LoadAsync(db, id, p.UserId());
            if (s is null) return Results.NotFound();
            var e = await db.Evaluations.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == id);
            var m = await db.DeliveryMetrics.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == id);
            return Results.Ok(new
            {
                session = SessionDto(s),
                evaluation = e is null ? null : new
                {
                    e.OverallScore,
                    e.ModelVersion,
                    e.HelpfulRating,
                    e.CreatedAt,
                    scores = JsonSerializer.Deserialize<JsonElement>(e.ScoresJson),
                    feedback = JsonSerializer.Deserialize<JsonElement>(e.FeedbackJson),
                },
                delivery = m,
            });
        });

        g.MapPost("/{id:guid}/rating", async (Guid id, FeedbackRatingRequest r, ClaimsPrincipal p, AppDbContext db) =>
        {
            if (r.Rating is < 1 or > 5) return Http.Problem(400, "Rating must be between 1 and 5.");
            var owner = p.UserId();
            var e = await db.Evaluations.FirstOrDefaultAsync(x => x.SessionId == id && db.Sessions.Any(s => s.Id == id && s.UserId == owner));
            if (e is null) return Results.NotFound();
            e.HelpfulRating = r.Rating;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static Task<Session?> LoadAsync(AppDbContext db, Guid id, Guid userId) =>
        db.Sessions.Include(s => s.Template).Include(s => s.Turns).FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

    private static async Task<bool> StreamTurnAsync(HttpContext ctx, InterviewService service, Session s)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        var (ended, _) = await service.NextInterviewerTurnAsync(s, delta => WriteEventAsync(ctx, new { delta }));
        try { await WriteEventAsync(ctx, new { done = true, ended }); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
        return ended;
    }

    private static async Task WriteEventAsync(HttpContext ctx, object payload)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, Web)}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    private static object SessionDto(Session s) => new
    {
        s.Id,
        s.Status,
        s.Mode,
        s.CreatedAt,
        s.StartedAt,
        s.EndedAt,
        s.FailureReason,
        category = s.Template?.Category,
        level = s.Template?.Difficulty,
        durationMin = s.Template?.DurationMin,
        turns = s.Turns.OrderBy(t => t.Seq).Select(t => new { t.Seq, t.Speaker, t.Text, t.StartMs, t.EndMs }),
    };
}
