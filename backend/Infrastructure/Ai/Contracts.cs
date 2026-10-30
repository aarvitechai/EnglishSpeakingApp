using System.Text.Json.Serialization;

namespace Infrastructure.Ai;

public record HistoryItem(string Speaker, string Text);

public record InterviewContext(
    string Category,
    string Difficulty,
    string Persona,
    IReadOnlyList<string> PlanQuestions,
    string? TargetRole,
    string? Resume,
    string? JobDescription,
    IReadOnlyList<HistoryItem> History,
    int AnswersGiven,
    int MaxAnswers)
{
    /// <summary>Code, not the model, decides when the interview ends, so length and cost stay bounded.</summary>
    public bool IsClosing => AnswersGiven >= MaxAnswers;
}

public record RubricDimension(string Key, string Name, double Weight, string Description, Dictionary<string, string> Levels);

public record RubricDocument(string Category, int Version, List<RubricDimension> Dimensions);

public record EvaluatorAnswer(int Seq, string Question, string Text);

public record EvaluatorInput(string Category, string Difficulty, RubricDocument Rubric, IReadOnlyList<EvaluatorAnswer> Answers);

public class DimensionScore
{
    [JsonPropertyName("dimension")] public string Dimension { get; set; } = "";
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("evidence")] public string Evidence { get; set; } = "";
}

public class AnswerScore
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("scores")] public List<DimensionScore> Scores { get; set; } = [];
}

public class Improvement
{
    [JsonPropertyName("skill")] public string Skill { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("quote")] public string Quote { get; set; } = "";
    [JsonPropertyName("why")] public string Why { get; set; } = "";
    [JsonPropertyName("rewrite")] public string Rewrite { get; set; } = "";
    [JsonPropertyName("drills")] public List<string> Drills { get; set; } = [];
}

public class Strength
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("quote")] public string Quote { get; set; } = "";
    [JsonPropertyName("why")] public string Why { get; set; } = "";
}

public class EvaluatorOutput
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("answers")] public List<AnswerScore> Answers { get; set; } = [];
    [JsonPropertyName("improvements")] public List<Improvement> Improvements { get; set; } = [];
    [JsonPropertyName("strengths")] public List<Strength> Strengths { get; set; } = [];
}

/// <summary>Accumulates token use for one call so the session's cost can be recorded.</summary>
public class AiUsage
{
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    public long CostPaise(double usdToInr)
    {
        var (input, output) = Model switch
        {
            "claude-haiku-5-5" => (0.10, 0.50),
            "claude-sonnet-5-5" => (2.0, 10.0),
            "claude-opus-5-5" => (4.0, 20.0),
            _ => (0.0, 0.0),
        };
        double usd = InputTokens / 1_000_000.0 * input + OutputTokens / 1_000_000.0 * output;
        return (long)Math.Ceiling(usd * usdToInr * 100);
    }
}

public interface IInterviewerAi
{
    IAsyncEnumerable<string> StreamAsync(InterviewContext context, AiUsage usage, CancellationToken ct);
}

public interface IEvaluatorAi
{
    string ModelVersion { get; }
    Task<EvaluatorOutput> EvaluateAsync(EvaluatorInput input, AiUsage usage, CancellationToken ct);
}

public class AiOptions
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string InterviewerModel { get; set; } = "claude-haiku-5-5";
    public string EvaluatorModel { get; set; } = "claude-opus-5-5";
    public double UsdToInr { get; set; } = 88;
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}
