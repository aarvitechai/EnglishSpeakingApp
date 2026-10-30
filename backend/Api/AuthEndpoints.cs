using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Domain;
using Google.Apis.Auth;
using Hangfire.Dashboard;
using Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Api;

public static class Http
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);
}

public class ReportsHub : Hub;

public class AdminDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => context.GetHttpContext().User.IsInRole(Roles.Admin);
}

public record RegisterRequest(string Email, string Name, string Password, bool AcceptTerms);
public record LoginRequest(string Email, string Password);
public record GoogleRequest(string IdToken, bool AcceptTerms);

public static class AuthEndpoints
{
    private static readonly PasswordHasher<User> Hasher = new();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        g.MapPost("/register", async (RegisterRequest r, AppDbContext db, HttpContext ctx, IConfiguration config) =>
        {
            var email = r.Email?.Trim().ToLowerInvariant() ?? "";
            if (!MailAddress.TryCreate(email, out _)) return Http.Problem(400, "Please enter a valid email address.");
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) return Http.Problem(400, "Please enter your name.");
            if (r.Password is null || r.Password.Length < 8) return Http.Problem(400, "Password must be at least 8 characters.");
            if (!r.AcceptTerms) return Http.Problem(400, "Please accept the terms and privacy notice to continue.");
            if (await db.Users.AnyAsync(u => u.Email == email)) return Http.Problem(409, "An account with this email already exists.");

            var user = new User { Email = email, Name = r.Name.Trim(), TermsAcceptedAt = DateTime.UtcNow, Role = RoleFor(email, config) };
            user.PasswordHash = Hasher.HashPassword(user, r.Password);
            db.Users.Add(user);
            db.Profiles.Add(new Profile { UserId = user.Id });
            await db.SaveChangesAsync();
            await SignInAsync(ctx, user);
            return Results.Ok(Dto(user));
        });

        g.MapPost("/login", async (LoginRequest r, AppDbContext db, HttpContext ctx) =>
        {
            var email = r.Email?.Trim().ToLowerInvariant() ?? "";
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            // Same message for unknown email and wrong password, so accounts cannot be enumerated.
            if (user?.PasswordHash is null || Hasher.VerifyHashedPassword(user, user.PasswordHash, r.Password ?? "") == PasswordVerificationResult.Failed)
                return Http.Problem(401, "Incorrect email or password.");
            await SignInAsync(ctx, user);
            return Results.Ok(Dto(user));
        });

        g.MapPost("/google", async (GoogleRequest r, AppDbContext db, HttpContext ctx, IConfiguration config) =>
        {
            var clientId = config["Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId)) return Http.Problem(503, "Google sign-in is not configured.");

            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await GoogleJsonWebSignature.ValidateAsync(r.IdToken, new GoogleJsonWebSignature.ValidationSettings { Audience = [clientId] });
            }
            catch (InvalidJwtException)
            {
                return Http.Problem(401, "Google sign-in failed.");
            }
            if (!payload.EmailVerified) return Http.Problem(401, "Your Google email address is not verified.");

            var email = payload.Email.ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null)
            {
                if (!r.AcceptTerms) return Http.Problem(400, "Please accept the terms and privacy notice to continue.");
                user = new User { Email = email, Name = payload.Name ?? email, GoogleSubject = payload.Subject, TermsAcceptedAt = DateTime.UtcNow, Role = RoleFor(email, config) };
                db.Users.Add(user);
                db.Profiles.Add(new Profile { UserId = user.Id });
            }
            else
            {
                user.GoogleSubject ??= payload.Subject;
            }
            await db.SaveChangesAsync();
            await SignInAsync(ctx, user);
            return Results.Ok(Dto(user));
        });

        g.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        app.MapGet("/api/config", (IConfiguration config, Infrastructure.Ai.AiOptions ai, BillingOptions billing) => Results.Ok(new
        {
            googleClientId = config["Google:ClientId"],
            aiEnabled = ai.Enabled,
            billingEnabled = billing.Enabled,
            proPricePaise = billing.ProPricePaise,
        }));

        var me = app.MapGroup("/api/me").RequireAuthorization();

        me.MapGet("/", async (ClaimsPrincipal p, AppDbContext db) =>
        {
            var id = p.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == id);
            return Results.Ok(Dto(user));
        });

        // DPDP/GDPR: everything we hold about the user, in one download.
        me.MapGet("/export", async (ClaimsPrincipal p, AppDbContext db) =>
        {
            var id = p.UserId();
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
            var sessions = await db.Sessions.AsNoTracking().Include(s => s.Turns).Where(s => s.UserId == id).ToListAsync();
            var ids = sessions.Select(s => s.Id).ToList();
            var export = new
            {
                exportedAt = DateTime.UtcNow,
                account = new { user.Email, user.Name, user.Plan, user.CreatedAt, user.TermsAcceptedAt },
                profile = await db.Profiles.AsNoTracking().Where(x => x.UserId == id)
                    .Select(x => new { x.TargetRole, x.ExperienceLevel, x.ParsedResumeJson }).FirstOrDefaultAsync(),
                sessions = sessions.Select(s => new
                {
                    s.Id, s.Status, s.Mode, s.JobDescription, s.CreatedAt, s.StartedAt, s.EndedAt,
                    turns = s.Turns.OrderBy(t => t.Seq).Select(t => new { t.Seq, t.Speaker, t.Text, t.StartMs, t.EndMs }),
                }),
                evaluations = await db.Evaluations.AsNoTracking().Where(e => ids.Contains(e.SessionId))
                    .Select(e => new { e.SessionId, e.OverallScore, e.ScoresJson, e.FeedbackJson, e.ModelVersion, e.CreatedAt }).ToListAsync(),
                deliveryMetrics = await db.DeliveryMetrics.AsNoTracking().Where(m => ids.Contains(m.SessionId)).ToListAsync(),
                practicePlans = await db.PracticePlans.AsNoTracking().Where(x => x.UserId == id)
                    .Select(x => new { x.Skill, x.DrillsJson, x.Status, x.DueDate }).ToListAsync(),
            };
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                "application/json", "interview-practice-data.json");
        });

        me.MapDelete("/", async (ClaimsPrincipal p, AppDbContext db, HttpContext ctx) =>
        {
            var id = p.UserId();
            // Cascades remove profile, sessions, turns, reports and plans; billing records are unlinked and kept.
            await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
    }

    private static string RoleFor(string email, IConfiguration config)
    {
        var admins = config.GetSection("Admin:Emails").Get<string[]>() ?? [];
        return admins.Contains(email, StringComparer.OrdinalIgnoreCase) ? Roles.Admin : Roles.Candidate;
    }

    private static async Task SignInAsync(HttpContext ctx, User user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, user.Role),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    private static object Dto(User u) => new { u.Id, u.Email, u.Name, u.Role, u.Plan };
}
