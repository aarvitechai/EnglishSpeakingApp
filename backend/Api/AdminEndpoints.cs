using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using Domain;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api;

public record SetStatusRequest(bool Disabled);
public record SetRoleRequest(string Role);
public record GrantPlanRequest(string Plan, int Days);
public record CreateStaffRequest(string Email, string Name, string Role);

/// <summary>
/// Admins support candidates. Super admins also manage staff, plans, payments and the audit trail.
/// Staff see account details and counts, never interview transcripts or resumes.
/// </summary>
public static class AdminEndpoints
{
    private const int PageSize = 25;
    private static readonly string[] StaffRoles = [Roles.Admin, Roles.SuperAdmin];

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var staff = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole(StaffRoles));
        var super = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole(Roles.SuperAdmin));

        staff.MapGet("/metrics", async (AppDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var users = await db.Users.CountAsync(u => u.Role == Roles.Candidate);
            var activated = await db.Users.CountAsync(u => u.Role == Roles.Candidate && db.Sessions.Any(s =>
                s.UserId == u.Id && s.Status == SessionStatus.Completed && s.CreatedAt <= u.CreatedAt.AddHours(24)));
            var started = await db.Sessions.CountAsync(s => s.Status != SessionStatus.Created);
            var completed = await db.Sessions.CountAsync(s => s.Status == SessionStatus.Completed);
            var paid = await db.Subscriptions
                .Where(s => s.Status == SubscriptionStatus.Active && s.RenewsAt > now)
                .GroupBy(s => s.Plan).Select(g => new { plan = g.Key, users = g.Select(s => s.UserId).Distinct().Count() }).ToListAsync();
            return Results.Ok(new
            {
                users,
                activationRate = users == 0 ? 0 : Math.Round((double)activated / users, 3),
                sessionsStarted = started,
                sessionsCompleted = completed,
                completionRate = started == 0 ? 0 : Math.Round((double)completed / started, 3),
                avgCostPaisePerCompleted = await db.Sessions.Where(s => s.Status == SessionStatus.Completed).AverageAsync(s => (double?)s.CostPaise),
                avgHelpfulRating = await db.Evaluations.Where(e => e.HelpfulRating != null).AverageAsync(e => (double?)e.HelpfulRating),
                paid,
            });
        });

        staff.MapGet("/users", async (string? q, int? page, AppDbContext db) =>
        {
            var query = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(u => u.Email.Contains(term) || u.Name.Contains(term));
            }
            var total = await query.CountAsync();
            int current = Math.Max(page ?? 1, 1);
            var now = DateTime.UtcNow;
            var rows = await query.OrderByDescending(u => u.CreatedAt).Skip((current - 1) * PageSize).Take(PageSize)
                .Select(u => new
                {
                    u.Id, u.Email, u.Name, u.Role, u.IsDisabled, u.CreatedAt, u.LastLoginAt,
                    sessions = db.Sessions.Count(s => s.UserId == u.Id),
                    plans = db.Subscriptions.Where(s => s.UserId == u.Id && s.Status == SubscriptionStatus.Active && s.RenewsAt > now).Select(s => s.Plan).ToList(),
                })
                .ToListAsync();
            return Results.Ok(new
            {
                total,
                page = current,
                pageSize = PageSize,
                users = rows.Select(u => new
                {
                    u.Id, u.Email, u.Name, u.Role, u.IsDisabled, u.CreatedAt, u.LastLoginAt, u.sessions,
                    plan = u.plans.OrderByDescending(Plans.Rank).FirstOrDefault() ?? Plans.Free,
                }),
            });
        });

        staff.MapPost("/users/{id:guid}/status", async (Guid id, SetStatusRequest r, ClaimsPrincipal p, AppDbContext db) =>
        {
            var (actor, target, denied) = await LoadAsync(db, p, id);
            if (denied is not null) return denied;
            if (r.Disabled && await IsLastSuperAdminAsync(db, target!)) return Http.Problem(409, "You cannot disable the only super admin.");
            target!.IsDisabled = r.Disabled;
            target.SecurityStamp = Guid.NewGuid();
            Audit(db, actor!, r.Disabled ? "user.disable" : "user.enable", target);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        staff.MapPost("/users/{id:guid}/reset-password", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
        {
            var (actor, target, denied) = await LoadAsync(db, p, id);
            if (denied is not null) return denied;
            var password = NewTemporaryPassword();
            target!.PasswordHash = AuthEndpoints.Hasher.HashPassword(target, password);
            target.MustChangePassword = true;
            target.LockedUntil = null;
            target.FailedLogins = 0;
            target.SecurityStamp = Guid.NewGuid();
            Audit(db, actor!, "user.reset_password", target);
            await db.SaveChangesAsync();
            return Results.Ok(new { temporaryPassword = password });
        });

        super.MapPost("/users/{id:guid}/role", async (Guid id, SetRoleRequest r, ClaimsPrincipal p, AppDbContext db) =>
        {
            if (r.Role is not (Roles.Candidate or Roles.Admin or Roles.SuperAdmin)) return Http.Problem(400, "Please choose a valid role.");
            var (actor, target, denied) = await LoadAsync(db, p, id);
            if (denied is not null) return denied;
            var previous = target!.Role;
            target.Role = r.Role;
            target.SecurityStamp = Guid.NewGuid();
            Audit(db, actor!, "user.set_role", target, $"{previous} to {r.Role}");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        super.MapPost("/users/{id:guid}/plan", async (Guid id, GrantPlanRequest r, ClaimsPrincipal p, AppDbContext db) =>
        {
            if (!Plans.All.Contains(r.Plan)) return Http.Problem(400, "Please choose a valid plan.");
            if (r.Plan != Plans.Free && r.Days is < 1 or > 366) return Http.Problem(400, "Days must be between 1 and 366.");
            var actor = await db.Users.FirstAsync(u => u.Id == p.UserId());
            var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (target is null) return Results.NotFound();

            // Replaces whatever is active. Payment records stay untouched for accounting.
            var active = await db.Subscriptions.Where(s => s.UserId == id && s.Status == SubscriptionStatus.Active).ToListAsync();
            foreach (var s in active) s.Status = SubscriptionStatus.Cancelled;
            if (r.Plan != Plans.Free)
                db.Subscriptions.Add(new Subscription { UserId = id, Plan = r.Plan, Status = SubscriptionStatus.Active, RenewsAt = DateTime.UtcNow.AddDays(r.Days) });
            Audit(db, actor, "user.set_plan", target, r.Plan == Plans.Free ? "free" : $"{r.Plan} for {r.Days} days");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        super.MapPost("/staff", async (CreateStaffRequest r, ClaimsPrincipal p, AppDbContext db) =>
        {
            var email = r.Email?.Trim().ToLowerInvariant() ?? "";
            if (!MailAddress.TryCreate(email, out _)) return Http.Problem(400, "Please enter a valid email address.");
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) return Http.Problem(400, "Please enter a name.");
            if (r.Role is not (Roles.Admin or Roles.SuperAdmin)) return Http.Problem(400, "Please choose a valid role.");
            if (await db.Users.AnyAsync(u => u.Email == email)) return Http.Problem(409, "An account with this email already exists. Change its role instead.");

            var actor = await db.Users.FirstAsync(u => u.Id == p.UserId());
            var password = NewTemporaryPassword();
            var user = new User { Email = email, Name = r.Name.Trim(), Role = r.Role, TermsAcceptedAt = DateTime.UtcNow, MustChangePassword = true };
            user.PasswordHash = AuthEndpoints.Hasher.HashPassword(user, password);
            db.Users.Add(user);
            db.Profiles.Add(new Profile { UserId = user.Id });
            Audit(db, actor, "staff.create", user, r.Role);
            await db.SaveChangesAsync();
            return Results.Ok(new { user.Id, temporaryPassword = password });
        });

        super.MapGet("/audit", async (AppDbContext db) =>
            Results.Ok(await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(200).ToListAsync()));

        super.MapGet("/payments", async (AppDbContext db) =>
        {
            var rows = await db.Payments.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200)
                .Select(x => new
                {
                    x.Id, x.AmountPaise, x.Status, x.ProviderRef, x.CreatedAt, x.Subscription!.Plan,
                    email = db.Users.Where(u => u.Id == x.Subscription.UserId).Select(u => u.Email).FirstOrDefault(),
                }).ToListAsync();
            var revenue = await db.Payments.Where(x => x.Status == PaymentStatus.Captured).SumAsync(x => (long?)x.AmountPaise) ?? 0;
            return Results.Ok(new { revenuePaise = revenue, payments = rows });
        });
    }

    /// <summary>Loads actor and target and applies the hierarchy: nobody acts on themselves, and admins act only on candidates.</summary>
    private static async Task<(User? Actor, User? Target, IResult? Denied)> LoadAsync(AppDbContext db, ClaimsPrincipal p, Guid targetId)
    {
        var actor = await db.Users.FirstAsync(u => u.Id == p.UserId());
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetId);
        if (target is null) return (null, null, Results.NotFound());
        if (target.Id == actor.Id) return (null, null, Http.Problem(409, "You cannot change your own account from here."));
        if (actor.Role != Roles.SuperAdmin && target.Role != Roles.Candidate)
            return (null, null, Http.Problem(403, "Only a super admin can manage staff accounts."));
        return (actor, target, null);
    }

    private static async Task<bool> IsLastSuperAdminAsync(AppDbContext db, User target) =>
        target.Role == Roles.SuperAdmin && await db.Users.CountAsync(u => u.Role == Roles.SuperAdmin && !u.IsDisabled) <= 1;

    private static void Audit(AppDbContext db, User actor, string action, User target, string? detail = null) =>
        db.AuditLogs.Add(new AuditLog { ActorId = actor.Id, ActorEmail = actor.Email, Action = action, TargetUserId = target.Id, TargetEmail = target.Email, Detail = detail });

    private static string NewTemporaryPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', 'x').Replace('/', 'y');
}

/// <summary>
/// Creates the first super admin from configuration, so no default password ever exists. It never promotes an
/// existing account: sign-up does not verify email addresses, so someone else could have registered that address.
/// </summary>
public static class AdminBootstrap
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var log = services.GetRequiredService<ILogger<AppDbContext>>();
        var email = config["SuperAdmin:Email"]?.Trim().ToLowerInvariant();
        var password = config["SuperAdmin:Password"];
        var db = services.GetRequiredService<AppDbContext>();

        if (await db.Users.AnyAsync(u => u.Role == Roles.SuperAdmin)) return;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            log.LogWarning("No super admin exists. Set SuperAdmin:Email and SuperAdmin:Password to create one.");
            return;
        }
        if (password.Length < 12) throw new InvalidOperationException("SuperAdmin:Password must be at least 12 characters.");
        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            log.LogError("Cannot create the super admin: {Email} is already registered. Choose a different SuperAdmin:Email.", email);
            return;
        }

        var user = new User { Email = email, Name = "Super Admin", Role = Roles.SuperAdmin, TermsAcceptedAt = DateTime.UtcNow, MustChangePassword = true };
        user.PasswordHash = AuthEndpoints.Hasher.HashPassword(user, password);
        db.Users.Add(user);
        db.Profiles.Add(new Profile { UserId = user.Id });
        await db.SaveChangesAsync();
        log.LogInformation("Created super admin {Email}.", email);
    }
}
