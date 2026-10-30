using System.Text.Json;
using System.Text.RegularExpressions;
using Domain;
using Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public record DimensionResult(string Key, string Name, double Score, double Weight);

public record ScoresDocument(List<DimensionResult> Dimensions, List<AnswerScore> Answers);

public record FeedbackDocument(string Summary, List<Improvement> Improvements, List<Strength> Strengths);

public class EvaluationService(AppDbContext db, IEvaluatorAi evaluator, AiOptions options)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Scores a finished session. Re-running replaces the earlier result, so job retries are safe.</summary>
    public async Task RunAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions
            .Include(s => s.Template!).ThenInclude(t => t.Rubric)
            .Include(s => s.Turns)
            .FirstAsync(s => s.Id == sessionId, ct);

        var turns = session.Turns.OrderBy(t => t.Seq).ToList();
        var rubric = JsonSerializer.Deserialize<RubricDocument>(session.Template!.Rubric!.DimensionsJson, Web)!;
        var input = new EvaluatorInput(session.Template.Category, session.Template.Difficulty, rubric, PairAnswers(turns));

        var usage = new AiUsage();
        var output = await evaluator.EvaluateAsync(input, usage, ct);
        var (scores, feedback, overall) = Aggregate(input, output);

        await db.Evaluations.Where(e => e.SessionId == sessionId).ExecuteDeleteAsync(ct);
        await db.DeliveryMetrics.Where(m => m.SessionId == sessionId).ExecuteDeleteAsync(ct);
        await db.PracticePlans.Where(p => p.SourceSessionId == sessionId).ExecuteDeleteAsync(ct);

        db.Evaluations.Add(new Evaluation
        {
            SessionId = sessionId,
            RubricVersion = rubric.Version,
            OverallScore = overall,
            ScoresJson = JsonSerializer.Serialize(scores, Web),
            FeedbackJson = JsonSerializer.Serialize(feedback, Web),
            ModelVersion = evaluator.ModelVersion,
        });
        db.DeliveryMetrics.Add(DeliveryCalculator.Compute(sessionId, turns));
        foreach (var (improvement, i) in feedback.Improvements.Select((x, i) => (x, i)))
        {
            db.PracticePlans.Add(new PracticePlan
            {
                UserId = session.UserId,
                SourceSessionId = sessionId,
                Skill = improvement.Skill,
                DrillsJson = JsonSerializer.Serialize(improvement.Drills, Web),
                DueDate = DateTime.UtcNow.Date.AddDays(2 * (i + 1)),
            });
        }

        session.CostPaise += usage.CostPaise(options.UsdToInr);
        session.Status = SessionStatus.Completed;
        session.FailureReason = null;
        await db.SaveChangesAsync(ct);
    }

    public static List<EvaluatorAnswer> PairAnswers(IReadOnlyList<Turn> turns)
    {
        var answers = new List<EvaluatorAnswer>();
        string lastQuestion = "";
        foreach (var turn in turns)
        {
            if (turn.Speaker == Speakers.Interviewer) lastQuestion = turn.Text;
            else answers.Add(new EvaluatorAnswer(turn.Seq, lastQuestion, turn.Text));
        }
        return answers;
    }

    /// <summary>
    /// The model scores; code aggregates and polices. Scores are clamped, unknown dimensions dropped, and any
    /// quote that is not actually in the candidate's answer is replaced, so a critique can never cite words
    /// the candidate did not say.
    /// </summary>
    public static (ScoresDocument Scores, FeedbackDocument Feedback, int Overall) Aggregate(EvaluatorInput input, EvaluatorOutput output)
    {
        var bySeq = input.Answers.ToDictionary(a => a.Seq);
        var known = input.Rubric.Dimensions.Select(d => d.Key).ToHashSet();

        var answers = output.Answers.Where(a => bySeq.ContainsKey(a.Seq)).Select(a => new AnswerScore
        {
            Seq = a.Seq,
            Scores = a.Scores.Where(s => known.Contains(s.Dimension)).Select(s => new DimensionScore
            {
                Dimension = s.Dimension,
                Score = Math.Clamp(s.Score, 1, 5),
                Evidence = VerifiedQuote(s.Evidence, bySeq[a.Seq].Text),
            }).ToList(),
        }).ToList();

        var dimensions = input.Rubric.Dimensions.Select(d =>
        {
            var values = answers.SelectMany(a => a.Scores).Where(s => s.Dimension == d.Key).Select(s => (double)s.Score).ToList();
            return new DimensionResult(d.Key, d.Name, values.Count > 0 ? Math.Round(values.Average(), 2) : 1, d.Weight);
        }).ToList();

        double totalWeight = dimensions.Sum(d => d.Weight);
        double weighted = totalWeight > 0 ? dimensions.Sum(d => d.Score * d.Weight) / totalWeight : 1;
        int overall = (int)Math.Round((weighted - 1) / 4 * 100);

        var improvements = output.Improvements.Where(i => bySeq.ContainsKey(i.Seq)).Take(3).ToList();
        foreach (var i in improvements) i.Quote = VerifiedQuote(i.Quote, bySeq[i.Seq].Text);
        var strengths = output.Strengths.Where(s => bySeq.ContainsKey(s.Seq)).ToList();
        foreach (var s in strengths) s.Quote = VerifiedQuote(s.Quote, bySeq[s.Seq].Text);

        return (new ScoresDocument(dimensions, answers), new FeedbackDocument(output.Summary, improvements, strengths), overall);
    }

    public static string VerifiedQuote(string quote, string answer)
    {
        if (!string.IsNullOrWhiteSpace(quote) && Normalize(answer).Contains(Normalize(quote))) return quote;
        return answer.Length <= 160 ? answer : answer[..160] + "…";
    }

    private static string Normalize(string s) => Regex.Replace(s, @"[^\p{L}\p{N}]+", " ").Trim().ToLowerInvariant();
}
