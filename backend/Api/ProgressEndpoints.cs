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
            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                plan,
                renewsAt = await db.Subscriptions.Where(s => s.UserId == id && s.Plan == plan && s.Status == SubscriptionStatus.Active && s.RenewsAt > now).MaxAsync(s => s.RenewsAt),
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
