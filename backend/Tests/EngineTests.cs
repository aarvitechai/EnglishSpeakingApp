using System.Text;
using System.Text.Json;
using Domain;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Infrastructure;
using Infrastructure.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

public class DeliveryTests
{
    [Fact]
    public void Metrics_are_counted_in_code()
    {
        var id = Guid.NewGuid();
        var m = DeliveryCalculator.Compute(id,
        [
            new Turn { Seq = 1, Speaker = Speakers.Interviewer, Text = "Tell me about yourself please" },
            new Turn { Seq = 2, Speaker = Speakers.Candidate, Text = "Um I am, you know, a basically good student", StartMs = 0, EndMs = 6000, LongPauses = 2 },
        ]);
        Assert.Equal(3, m.FillerCount);
        Assert.Equal(90, m.Wpm); // 9 words in 6 seconds
        Assert.Equal(2, m.LongPauses);
        Assert.Equal(6, m.AvgAnswerSec);
        Assert.Equal(0.643, m.TalkRatio); // 9 of 14 words
    }

    [Fact]
    public void Typed_answers_have_no_speaking_pace()
    {
        var m = DeliveryCalculator.Compute(Guid.NewGuid(), [new Turn { Seq = 1, Speaker = Speakers.Candidate, Text = "typed answer" }]);
        Assert.Null(m.Wpm);
        Assert.Null(m.AvgAnswerSec);
    }
}

public class AggregationTests
{
    private static readonly RubricDocument Rubric = new("hr", 1,
    [
        new RubricDimension("a", "A", 0.75, "", []),
        new RubricDimension("b", "B", 0.25, "", []),
    ]);

    private static readonly EvaluatorInput Input = new("hr", "easy", Rubric, [new EvaluatorAnswer(2, "Q?", "I built the dashboard for my college.")]);

    [Fact]
    public void Scores_are_clamped_and_weighted_in_code()
    {
        var (scores, _, overall) = EvaluationService.Aggregate(Input, new EvaluatorOutput
        {
            Answers = [new AnswerScore { Seq = 2, Scores = [new DimensionScore { Dimension = "a", Score = 9 }, new DimensionScore { Dimension = "b", Score = 1 }, new DimensionScore { Dimension = "invented", Score = 5 }] }],
        });
        Assert.Equal(2, scores.Dimensions.Count);
        Assert.Equal(5, scores.Dimensions[0].Score);
        Assert.Equal(75, overall); // weighted 4 of 5 on a 1-5 scale
    }

    [Fact]
    public void A_fabricated_quote_is_replaced_with_the_candidates_real_words()
    {
        var (_, feedback, _) = EvaluationService.Aggregate(Input, new EvaluatorOutput
        {
            Improvements = [new Improvement { Seq = 2, Quote = "I single-handedly saved the company" }, new Improvement { Seq = 99, Quote = "ghost answer" }],
        });
        var only = Assert.Single(feedback.Improvements);
        Assert.Equal("I built the dashboard for my college.", only.Quote);
    }

    [Fact]
    public void A_real_quote_survives_punctuation_differences()
    {
        Assert.Equal("built the dashboard", EvaluationService.VerifiedQuote("built the dashboard", "I built, the dashboard!"));
    }
}

public class ScriptedInterviewerTests
{
    private static InterviewContext Ctx(params HistoryItem[] history) => new("hr", "medium", "p", ["Q one?", "Q two?"], null, null, null, history, history.Count(h => h.Speaker == Speakers.Candidate), 4);

    [Fact]
    public void Follows_up_once_on_a_vague_answer_using_the_candidates_words_then_moves_on()
    {
        var followUp = ScriptedInterviewer.NextLine(Ctx(
            new(Speakers.Interviewer, "Hello. Q one?"),
            new(Speakers.Candidate, "I worked on a big college project with my friends last year")));
        Assert.Contains("You said", followUp);

        var next = ScriptedInterviewer.NextLine(Ctx(
            new(Speakers.Interviewer, "Hello. Q one?"),
            new(Speakers.Candidate, "I worked on a project"),
            new(Speakers.Interviewer, followUp),
            new(Speakers.Candidate, "Still vague")));
        Assert.Contains("Q two?", next);
    }

    [Fact]
    public void Closes_when_the_answer_budget_is_used()
    {
        var c = Ctx() with { AnswersGiven = 4 };
        Assert.True(c.IsClosing);
        Assert.StartsWith("Thank you", ScriptedInterviewer.NextLine(c));
    }
}

public class ResumeParserTests
{
    [Fact]
    public void Reads_docx_and_finds_skills_without_false_matches()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("Anita Rao. Skills: C#, SQL, Machine Learning. Good at django-free cooking and going places.")))));
        }
        ms.Position = 0;
        var parsed = ResumeParser.Parse("cv.DOCX", ms);
        Assert.Contains("c#", parsed.Skills);
        Assert.Contains("machine learning", parsed.Skills);
        Assert.DoesNotContain("java", parsed.Skills);
        Assert.DoesNotContain("go", parsed.Skills.Where(s => s == "rust"));
    }

    [Fact]
    public void Rejects_unsupported_and_corrupt_files()
    {
        Assert.Throws<UnsupportedResumeException>(() => ResumeParser.Parse("cv.exe", new MemoryStream(Encoding.UTF8.GetBytes("MZ"))));
        Assert.Throws<UnsupportedResumeException>(() => ResumeParser.Parse("cv.pdf", new MemoryStream(Encoding.UTF8.GetBytes("not really a pdf"))));
    }
}

public class SignatureTests
{
    [Fact]
    public void Matches_a_known_hmac_and_rejects_tampering()
    {
        // HMAC-SHA256("order_1|pay_1", "secret"), computed independently.
        var sig = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData("secret"u8, "order_1|pay_1"u8));
        Assert.True(RazorpaySignature.IsValid("order_1|pay_1", sig, "secret"));
        Assert.False(RazorpaySignature.IsValid("order_1|pay_2", sig, "secret"));
        Assert.False(RazorpaySignature.IsValid("order_1|pay_1", sig, null));
    }
}

/// <summary>Regression suite from BUILD_PLAN section 8. Must pass in CI before prompt or scoring changes merge.</summary>
public class GoldenEvalTests
{
    private record Item(string Category, string Question, string Text, Dictionary<string, int> Expected);
    private record Golden(List<Item> Items);

    private static Golden Load() => JsonSerializer.Deserialize<Golden>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "evals", "golden", "answers.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static RubricDocument Rubric(string category) => JsonSerializer.Deserialize<RubricDocument>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "rubrics", $"{category}.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<(double Agreement, List<string> Misses)> AgreementAsync(IEvaluatorAi evaluator)
    {
        var misses = new List<string>();
        int within = 0, total = 0;
        foreach (var (item, i) in Load().Items.Select((x, i) => (x, i)))
        {
            var input = new EvaluatorInput(item.Category, "medium", Rubric(item.Category), [new EvaluatorAnswer(i, item.Question, item.Text)]);
            var output = await evaluator.EvaluateAsync(input, new AiUsage(), CancellationToken.None);
            foreach (var score in output.Answers.Single().Scores)
            {
                total++;
                if (Math.Abs(score.Score - item.Expected[score.Dimension]) <= 1) within++;
                else misses.Add($"{item.Text[..Math.Min(30, item.Text.Length)]} / {score.Dimension}: got {score.Score}, want {item.Expected[score.Dimension]}");
            }
        }
        return ((double)within / total, misses);
    }

    [Fact]
    public async Task Rule_based_scores_stay_within_one_of_the_labels_on_85_percent()
    {
        var (agreement, misses) = await AgreementAsync(new RuleBasedEvaluator());
        Assert.True(agreement >= 0.85, $"Agreement was {agreement:P0}. Misses: {string.Join(" | ", misses)}");
    }

    [Fact]
    public async Task Rule_based_scores_are_identical_across_five_reruns()
    {
        var rubric = Rubric("hr");
        var input = new EvaluatorInput("hr", "medium", rubric, [new EvaluatorAnswer(2, "Why?", Load().Items[1].Text)]);
        var runs = new HashSet<string>();
        for (int i = 0; i < 5; i++)
            runs.Add(JsonSerializer.Serialize(await new RuleBasedEvaluator().EvaluateAsync(input, new AiUsage(), CancellationToken.None)));
        Assert.Single(runs);
    }

    [Fact]
    public void Accent_style_wording_differences_do_not_change_scores()
    {
        // Same content, Indian-English phrasing. Scoring must depend on substance, not on this kind of variation.
        const string a = "In my final-year project I built a dashboard because staff tracked data by hand, and as a result we saved 6 hours weekly.";
        const string b = "In my final-year project only I built a dashboard itself because staff were tracking data by hand, and as a result we saved 6 hours weekly.";
        foreach (var dim in Rubric("behavioural").Dimensions)
        {
            int sa = RuleBasedEvaluator.ScoreDimension(dim.Key, new EvaluatorAnswer(1, "q", a));
            int sb = RuleBasedEvaluator.ScoreDimension(dim.Key, new EvaluatorAnswer(1, "q", b));
            Assert.True(Math.Abs(sa - sb) <= 1, $"{dim.Key}: {sa} vs {sb}");
        }
    }
}

/// <summary>Exercises the real Anthropic SDK against a local stub: request shape and response parsing, not model quality.</summary>
public class ClaudeWiringTests : IAsyncLifetime
{
    private WebApplication _stub = null!;
    private string _baseUrl = "";
    private string _lastBody = "";
    private string _lastBeta = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _stub = builder.Build();
        _stub.MapPost("/v1/messages", async (HttpContext ctx) =>
        {
            _lastBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            _lastBeta = ctx.Request.Headers["anthropic-beta"].ToString();
            if (JsonDocument.Parse(_lastBody).RootElement.TryGetProperty("stream", out var s) && s.GetBoolean())
            {
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.WriteAsync(Sse("message_start", """{"type":"message_start","message":{"id":"m","type":"message","role":"assistant","content":[],"model":"claude-haiku-5-5","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":100,"output_tokens":1}}}"""));
                await ctx.Response.WriteAsync(Sse("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""));
                await ctx.Response.WriteAsync(Sse("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello "}}"""));
                await ctx.Response.WriteAsync(Sse("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"there?"}}"""));
                await ctx.Response.WriteAsync(Sse("content_block_stop", """{"type":"content_block_stop","index":0}"""));
                await ctx.Response.WriteAsync(Sse("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":20}}"""));
                await ctx.Response.WriteAsync(Sse("message_stop", """{"type":"message_stop"}"""));
                return;
            }
            var text = JsonSerializer.Serialize(new EvaluatorOutput { Summary = "ok" });
            await ctx.Response.WriteAsJsonAsync(new
            {
                id = "m", type = "message", role = "assistant", model = "claude-opus-5-5",
                content = new[] { new { type = "text", text } },
                stop_reason = "end_turn", stop_sequence = (string?)null,
                usage = new { input_tokens = 1000, output_tokens = 500 },
            });
        });
        await _stub.StartAsync();
        _baseUrl = _stub.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    private static string Sse(string name, string data) => $"event: {name}\ndata: {data}\n\n";

    private (AiOptions, PromptLibrary) Setup() => (
        new AiOptions { ApiKey = "test", BaseUrl = _baseUrl },
        new PromptLibrary(Path.Combine(AppContext.BaseDirectory, "content")));

    [Fact]
    public async Task Interviewer_streams_text_and_records_usage()
    {
        var (options, prompts) = Setup();
        var ai = new ClaudeInterviewer(AnthropicClientFactory.Create(options), options, prompts);
        var ctx = new InterviewContext("hr", "easy", "Warm", ["Q1?"], "SDE", "<resume>", "<jd>", [new(Speakers.Interviewer, "Hi"), new(Speakers.Candidate, "Hello")], 1, 3);

        var usage = new AiUsage();
        var text = new StringBuilder();
        await foreach (var d in ai.StreamAsync(ctx, usage, CancellationToken.None)) text.Append(d);

        Assert.Equal("Hello there?", text.ToString());
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);

        var body = JsonDocument.Parse(_lastBody).RootElement;
        Assert.Equal("claude-haiku-5-5", body.GetProperty("model").GetString());
        Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.Equal("user", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", body.GetProperty("messages").EnumerateArray().Last().GetProperty("role").GetString());
        Assert.Contains("<candidate_resume>", body.GetProperty("system").GetString());
    }

    [Fact]
    public async Task Evaluator_requests_structured_output_with_refusal_fallback()
    {
        var (options, prompts) = Setup();
        var ai = new ClaudeEvaluator(AnthropicClientFactory.Create(options), options, prompts);
        var rubric = new RubricDocument("hr", 1, [new RubricDimension("clarity", "Clarity", 1, "d", [])]);
        var usage = new AiUsage();
        var result = await ai.EvaluateAsync(new EvaluatorInput("hr", "easy", rubric, [new EvaluatorAnswer(2, "Q", "A")]), usage, CancellationToken.None);

        Assert.Equal("ok", result.Summary);
        Assert.Equal(1000, usage.InputTokens);
        // 1000 input + 500 output on Opus: 0.014 USD, 1.232 INR at 88, so 124 paise rounded up.
        Assert.Equal(124, usage.CostPaise(88));

        var body = JsonDocument.Parse(_lastBody).RootElement;
        Assert.Equal("claude-opus-5-5", body.GetProperty("model").GetString());
        Assert.Equal("json_schema", body.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("default", body.GetProperty("fallbacks").GetString());
        Assert.Contains("server-side-fallback-2026-07-01", _lastBeta);
    }
}
