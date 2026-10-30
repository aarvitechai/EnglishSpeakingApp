using System.Security.Claims;
using System.Text.Json;
using Domain;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api;

public record ProfileRequest(string? TargetRole, string? ExperienceLevel);

public static class ProfileEndpoints
{
    public const long MaxResumeBytes = 5 * 1024 * 1024;

    public static void MapProfileEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/profile").RequireAuthorization();

        g.MapGet("/", async (ClaimsPrincipal p, AppDbContext db) => Results.Ok(ToDto(await GetAsync(db, p.UserId()))));

        g.MapPut("/", async (ProfileRequest r, ClaimsPrincipal p, AppDbContext db) =>
        {
            var profile = await GetAsync(db, p.UserId());
            profile.TargetRole = Clip(r.TargetRole, 200);
            profile.ExperienceLevel = Clip(r.ExperienceLevel, 50);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(profile));
        });

        g.MapPost("/resume", async (IFormFile file, ClaimsPrincipal p, AppDbContext db) =>
        {
            if (file.Length == 0 || file.Length > MaxResumeBytes) return Http.Problem(400, "Please upload a file smaller than 5 MB.");
            ParsedResume parsed;
            try
            {
                await using var stream = file.OpenReadStream();
                parsed = ResumeParser.Parse(file.FileName, stream);
            }
            catch (UnsupportedResumeException e)
            {
                return Http.Problem(400, e.Message);
            }

            var profile = await GetAsync(db, p.UserId());
            profile.ParsedResumeJson = JsonSerializer.Serialize(new { fileName = Path.GetFileName(file.FileName), parsed.Text, parsed.Skills }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(profile));
        }).DisableAntiforgery();

        g.MapDelete("/resume", async (ClaimsPrincipal p, AppDbContext db) =>
        {
            var profile = await GetAsync(db, p.UserId());
            profile.ParsedResumeJson = null;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(profile));
        });
    }

    private static async Task<Profile> GetAsync(AppDbContext db, Guid userId)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(x => x.UserId == userId);
        if (profile is null)
        {
            profile = new Profile { UserId = userId };
            db.Profiles.Add(profile);
        }
        return profile;
    }

    private static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= max ? s : s[..max];
    }

    private static object ToDto(Profile x)
    {
        string? fileName = null;
        string[] skills = [];
        if (x.ParsedResumeJson is not null)
        {
            using var doc = JsonDocument.Parse(x.ParsedResumeJson);
            fileName = doc.RootElement.GetProperty("fileName").GetString();
            skills = doc.RootElement.GetProperty("skills").EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
        return new { x.TargetRole, x.ExperienceLevel, resumeFileName = fileName, resumeSkills = skills };
    }
}
