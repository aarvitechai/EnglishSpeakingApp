using System.Security.Claims;
using System.Text.Json;
using Domain;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api;

public record PracticePlanUpdate(string Status);

public static class ProgressEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapProgressEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/progress", async (ClaimsPrincipal p, AppDbContext db, InterviewService service) =>
        {
            var id = p.UserId();
            var rows = await (
                from s in db.Sessions.AsNoTracking()
                join e in db.Evaluations on s.Id equals e.SessionId
                where s.UserId == id && s.Status == SessionStatus.Completed
                orderby s.CreatedAt
                select new { s.Id, s.CreatedAt, s.Template!.Category, e.OverallScore, e.ScoresJson })
                .ToListAsync();

            var plan = await service.EffectivePlanAsync(id);
            return Results.Ok(new
            {
                plan,
                used = await service.UsedThisMonthAsync(id),
                limit = service.MonthlyLimit(plan),
                sessions = rows.Select(r => new { r.Id, r.CreatedAt, r.Category, score = r.OverallScore }),
                skills = rows
                    .SelectMany(r => JsonSerializer.Deserialize<ScoresDocument>(r.ScoresJson, Web)!.Dimensions)
                    .GroupBy(d => d.Name)
                    .Select(g => new
                    {
                        name = g.Key,
                        count = g.Count(),
                        first = g.First().Score,
                        latest = g.Last().Score,
                    })
                    .OrderBy(s => s.latest),
            });
        });

        g.MapGet("/practice-plans", async (ClaimsPrincipal p, AppDbContext db) =>
        {
            var id = p.UserId();
            var plans = await db.PracticePlans.AsNoTracking()
                .Where(x => x.UserId == id && x.Status == "open")
                .OrderBy(x => x.DueDate).Take(9).ToListAsync();
            return Results.Ok(plans.Select(x => new
            {
                x.Id, x.Skill, x.Status, x.DueDate, x.SourceSessionId,
                drills = JsonSerializer.Deserialize<string[]>(x.DrillsJson, Web),
            }));
        });

        g.MapPatch("/practice-plans/{id:guid}", async (Guid id, PracticePlanUpdate r, ClaimsPrincipal p, AppDbContext db) =>
        {
            if (r.Status is not ("open" or "done")) return Http.Problem(400, "Status must be open or done.");
            var owner = p.UserId();
            var plan = await db.PracticePlans.FirstOrDefaultAsync(x => x.Id == id && x.UserId == owner);
            if (plan is null) return Results.NotFound();
            plan.Status = r.Status;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public static class AdminEndpoints
{
    /// <summary>The BUILD_PLAN section 10 metrics that can be derived from our own tables.</summary>
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/metrics", async (AppDbContext db) =>
        {
            var users = await db.Users.CountAsync();
            var activated = await db.Users.CountAsync(u => db.Sessions.Any(s =>
                s.UserId == u.Id && s.Status == SessionStatus.Completed && s.CreatedAt <= u.CreatedAt.AddHours(24)));
            var started = await db.Sessions.CountAsync(s => s.Status != SessionStatus.Created);
            var completed = await db.Sessions.CountAsync(s => s.Status == SessionStatus.Completed);
            var ratings = db.Evaluations.Where(e => e.HelpfulRating != null).Select(e => (double?)e.HelpfulRating);
            return Results.Ok(new
            {
                users,
                activationRate = users == 0 ? 0 : Math.Round((double)activated / users, 3),
                sessionsStarted = started,
                sessionsCompleted = completed,
                completionRate = started == 0 ? 0 : Math.Round((double)completed / started, 3),
                avgCostPaisePerCompleted = await db.Sessions.Where(s => s.Status == SessionStatus.Completed).AverageAsync(s => (double?)s.CostPaise),
                avgHelpfulRating = await ratings.AverageAsync(),
                paidUsers = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active && s.RenewsAt > DateTime.UtcNow),
            });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
