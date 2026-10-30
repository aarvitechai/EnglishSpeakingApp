using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Domain;
using Beta = Anthropic.Models.Beta.Messages;

namespace Infrastructure.Ai;

public class PromptLibrary(string contentRoot)
{
    public string Interviewer { get; } = File.ReadAllText(Path.Combine(contentRoot, "prompts", "interviewer.md"));
    public string Evaluator { get; } = File.ReadAllText(Path.Combine(contentRoot, "prompts", "evaluator.md"));
}

public static class AnthropicClientFactory
{
    public static AnthropicClient Create(AiOptions o)
    {
        var client = new AnthropicClient { ApiKey = o.ApiKey };
        return string.IsNullOrWhiteSpace(o.BaseUrl) ? client : new AnthropicClient { ApiKey = o.ApiKey, BaseUrl = o.BaseUrl };
    }
}

public class ClaudeInterviewer(AnthropicClient client, AiOptions options, PromptLibrary prompts) : IInterviewerAi
{
    public async IAsyncEnumerable<string> StreamAsync(InterviewContext c, AiUsage usage, [EnumeratorCancellation] CancellationToken ct)
    {
        usage.Model = options.InterviewerModel;
        var parameters = new MessageCreateParams
        {
            Model = options.InterviewerModel,
            MaxTokens = 400,
            // Spoken replies need the lowest latency we can get; depth comes from the plan and evaluator.
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Thinking = new ThinkingConfigDisabled(),
            System = BuildSystem(c),
            Messages = BuildMessages(c),
        };

        await foreach (var ev in client.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickStart(out var start))
                usage.InputTokens += start.Message.Usage.InputTokens;
            else if (ev.TryPickDelta(out var delta))
                usage.OutputTokens = delta.Usage.OutputTokens;
            else if (ev.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var text))
                yield return text.Text;
        }
    }

    private string BuildSystem(InterviewContext c)
    {
        var s = new StringBuilder(prompts.Interviewer);
        s.Append("\n\n# This interview\n");
        s.Append($"Category: {c.Category}\nDifficulty: {c.Difficulty}\nPersona: {c.Persona}\n");
        if (!string.IsNullOrWhiteSpace(c.TargetRole)) s.Append($"Target role: {c.TargetRole}\n");
        s.Append("\nPlanned questions, in order:\n");
        for (int i = 0; i < c.PlanQuestions.Count; i++) s.Append($"{i + 1}. {c.PlanQuestions[i]}\n");
        if (!string.IsNullOrWhiteSpace(c.Resume)) s.Append($"\n<candidate_resume>\n{c.Resume}\n</candidate_resume>\n");
        if (!string.IsNullOrWhiteSpace(c.JobDescription)) s.Append($"\n<job_description>\n{c.JobDescription}\n</job_description>\n");
        s.Append($"\nThe candidate has given {c.AnswersGiven} of at most {c.MaxAnswers} answers. ");
        s.Append(c.IsClosing
            ? "The interview is over: thank the candidate in one or two sentences and ask nothing further."
            : "Ask exactly one question now.");
        return s.ToString();
    }

    private static List<MessageParam> BuildMessages(InterviewContext c)
    {
        var messages = new List<MessageParam> { new UserMessageParam { Content = "[The candidate has joined. Begin the interview.]" } };
        foreach (var h in c.History)
            messages.Add(h.Speaker == Speakers.Interviewer
                ? new AssistantMessageParam { Content = h.Text }
                : new UserMessageParam { Content = h.Text });
        return messages;
    }
}

public class ClaudeEvaluator(AnthropicClient client, AiOptions options, PromptLibrary prompts) : IEvaluatorAi
{
    public string ModelVersion => options.EvaluatorModel;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<EvaluatorOutput> EvaluateAsync(EvaluatorInput input, AiUsage usage, CancellationToken ct)
    {
        usage.Model = options.EvaluatorModel;
        var parameters = new Beta.MessageCreateParams
        {
            Model = options.EvaluatorModel,
            MaxTokens = 16000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Beta.Default(),
            OutputConfig = new Beta.BetaOutputConfig
            {
                Effort = Beta.Effort.Medium,
                Format = new Beta.BetaJsonOutputFormat { Schema = EvaluatorSchema.Value },
            },
            System = prompts.Evaluator,
            Messages = [new Beta.BetaUserMessageParam { Content = BuildUserMessage(input) }],
        };

        var response = await client.Beta.Messages.Create(parameters, ct);
        usage.InputTokens += response.Usage.InputTokens;
        usage.OutputTokens += response.Usage.OutputTokens;

        if (response.StopReason == "refusal") throw new InvalidOperationException("Evaluator declined the request.");
        if (response.StopReason == "max_tokens") throw new InvalidOperationException("Evaluator output was truncated.");

        var json = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return JsonSerializer.Deserialize<EvaluatorOutput>(json, Web)
            ?? throw new InvalidOperationException("Evaluator returned empty JSON.");
    }

    private static string BuildUserMessage(EvaluatorInput input)
    {
        var s = new StringBuilder();
        s.Append($"Category: {input.Category}\nDifficulty: {input.Difficulty}\n\n<rubric>\n");
        s.Append(JsonSerializer.Serialize(input.Rubric, Web));
        s.Append("\n</rubric>\n\n<transcript>\n");
        foreach (var a in input.Answers)
            s.Append($"<answer seq=\"{a.Seq}\">\n<question>{a.Question}</question>\n<candidate_said>{a.Text}</candidate_said>\n</answer>\n");
        s.Append("</transcript>");
        return s.ToString();
    }
}

internal static class EvaluatorSchema
{
    public static readonly Dictionary<string, JsonElement> Value = Build();

    private static object Obj(object properties, params string[] required) =>
        new { type = "object", additionalProperties = false, properties, required };

    private static Dictionary<string, JsonElement> Build()
    {
        var str = new { type = "string" };
        var integer = new { type = "integer" };
        var schema = Obj(new
        {
            summary = str,
            answers = new
            {
                type = "array",
                items = Obj(new
                {
                    seq = integer,
                    scores = new
                    {
                        type = "array",
                        items = Obj(new { dimension = str, score = integer, evidence = str }, "dimension", "score", "evidence"),
                    },
                }, "seq", "scores"),
            },
            improvements = new
            {
                type = "array",
                items = Obj(new
                {
                    skill = str, title = str, seq = integer, quote = str, why = str, rewrite = str,
                    drills = new { type = "array", items = str },
                }, "skill", "title", "seq", "quote", "why", "rewrite", "drills"),
            },
            strengths = new
            {
                type = "array",
                items = Obj(new { seq = integer, quote = str, why = str }, "seq", "quote", "why"),
            },
        }, "summary", "answers", "improvements", "strengths");

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(schema))!;
    }
}
