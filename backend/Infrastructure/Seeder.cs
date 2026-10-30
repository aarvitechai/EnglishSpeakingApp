using System.Text.Json;
using Domain;
using Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

/// <summary>Loads content/ into the database. Safe to run on every start: it only inserts what is missing.</summary>
public class Seeder(AppDbContext db, string contentRoot)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private record SeedQuestion(string Difficulty, string Text, string[] Tags, string? Hint);

    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var category in Categories.All)
        {
            var rubric = await SeedRubricAsync(category, ct);
            await SeedTemplatesAsync(category, rubric, ct);
            await SeedQuestionsAsync(category, ct);
        }
    }

    private async Task<Rubric> SeedRubricAsync(string category, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(contentRoot, "rubrics", $"{category}.json"), ct);
        var doc = JsonSerializer.Deserialize<RubricDocument>(json, Web)!;
        var rubric = await db.Rubrics.FirstOrDefaultAsync(r => r.Category == category && r.Version == doc.Version, ct);
        if (rubric is null)
        {
            rubric = new Rubric { Category = category, Version = doc.Version, DimensionsJson = json };
            db.Rubrics.Add(rubric);
            await db.SaveChangesAsync(ct);
        }
        return rubric;
    }

    private async Task SeedTemplatesAsync(string category, Rubric rubric, CancellationToken ct)
    {
        foreach (var level in Levels.All)
        {
            var template = await db.InterviewTemplates.FirstOrDefaultAsync(t => t.Category == category && t.Difficulty == level.Id, ct);
            if (template is null)
            {
                db.InterviewTemplates.Add(new InterviewTemplate { Category = category, Difficulty = level.Id, Persona = level.Persona, RubricId = rubric.Id, DurationMin = level.Minutes });
            }
            else
            {
                template.RubricId = rubric.Id;
                template.Persona = level.Persona;
                template.DurationMin = level.Minutes;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedQuestionsAsync(string category, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(contentRoot, "questions", $"{category}.json"), ct);
        var seeds = JsonSerializer.Deserialize<List<SeedQuestion>>(json, Web)!;
        var existing = await db.Questions.Where(q => q.Category == category).ToDictionaryAsync(q => q.Text, ct);
        foreach (var q in seeds)
        {
            if (existing.TryGetValue(q.Text, out var row))
            {
                row.Difficulty = q.Difficulty;
                row.Hint = q.Hint;
            }
            else
            {
                db.Questions.Add(new Question { Category = category, Difficulty = q.Difficulty, Text = q.Text, Tags = string.Join(',', q.Tags), Hint = q.Hint });
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
