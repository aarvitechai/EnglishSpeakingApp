using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Domain;

namespace Infrastructure.Ai;

/// <summary>
/// Offline interviewer used when no Anthropic key is configured, and as the fallback when an API call fails
/// mid-interview so a candidate is never stranded. Follow-ups are built from the candidate's own words.
/// </summary>
public class ScriptedInterviewer : IInterviewerAi
{
    public async IAsyncEnumerable<string> StreamAsync(InterviewContext c, AiUsage usage, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var piece in Regex.Split(NextLine(c), @"(?<=\s)"))
        {
            ct.ThrowIfCancellationRequested();
            yield return piece;
            await Task.Yield();
        }
    }

    public static string NextLine(InterviewContext c)
    {
        var asked = c.History.Where(h => h.Speaker == Speakers.Interviewer).Select(h => h.Text).ToList();
        var next = c.PlanQuestions.FirstOrDefault(q => !asked.Any(a => a.Contains(q, StringComparison.Ordinal)));

        if (c.IsClosing || (next is null && !NeedsFollowUp(c, asked)))
            return "Thank you, that's all the questions I have for today. I'll prepare your feedback report now.";

        if (asked.Count == 0)
            return $"Hello, thanks for joining. I'll be your interviewer for this {Label(c.Category)} practice round. Let's begin. {next}";

        if (NeedsFollowUp(c, asked))
            return FollowUp(c.History.Last(h => h.Speaker == Speakers.Candidate).Text);

        return $"Thanks. Next question: {next}";
    }

    private static bool NeedsFollowUp(InterviewContext c, List<string> asked)
    {
        if (c.History.Count == 0 || c.History[^1].Speaker != Speakers.Candidate) return false;
        // One follow-up per question at most: the previous interviewer line must be a plan question.
        if (!c.PlanQuestions.Any(q => asked[^1].Contains(q, StringComparison.Ordinal))) return false;
        var answer = c.History[^1].Text;
        return DeliveryCalculator.CountWords(answer) < 40 || (c.Difficulty != "easy" && !Regex.IsMatch(answer, @"\d"));
    }

    private static string FollowUp(string answer)
    {
        var words = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 8)
            return "Could you expand on that with one specific example from your studies, a project or an internship?";
        var phrase = string.Join(' ', words.Skip(Math.Max(0, words.Length / 2 - 3)).Take(6)).TrimEnd('.', ',', '!', '?');
        return $"You said \"{phrase}\". What exactly did you personally do there, and what was the measurable result?";
    }

    private static string Label(string category) => category switch
    {
        Categories.Behavioural => "behavioural",
        Categories.Technical => "technical",
        _ => "HR",
    };
}

/// <summary>
/// Deterministic, signal-based scoring. It measures structure and specificity, not content correctness,
/// so reports are labelled "rule-based" in the UI. Spelling and accent never affect the score.
/// </summary>
public partial class RuleBasedEvaluator : IEvaluatorAi
{
    public const string Version = "rule-based-v1";
    public string ModelVersion => Version;

    [GeneratedRegex(@"\bI (\w+ly )?(\w+ed|built|led|wrote|spoke|told|chose|made|took|ran|set up|taught|learnt|found|gave|began|drew)\b")]
    private static partial Regex PersonalAction();
    [GeneratedRegex(@"\b(weakness|strength|learned|learnt|realis|realiz|mistake|feedback|still|nervous|struggl|improv)", RegexOptions.IgnoreCase)]
    private static partial Regex ReflectionCue();
    [GeneratedRegex(@"\b(perfectionist|hard[- ]?work(er|ing)|work too hard)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cliche();
    [GeneratedRegex(@"\b(because|want|goal|interested|passion|enjoy)", RegexOptions.IgnoreCase)]
    private static partial Regex MotivationCue();
    [GeneratedRegex(@"\b(when|during|while|at my|in my|in our|in college|last (year|semester|summer)|final[- ]year|internship|project)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SituationCue();
    [GeneratedRegex(@"\b(i|we) (\w+ly )?(built|created|designed|led|organi[sz]ed|wrote|fixed|implemented|analy[sz]ed|decided|proposed|spoke|talked|learned|learnt|reduced|improved|tested|started|scheduled|convinced|negotiated|taught|managed|solved|used|chose|split|set up)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionCue();
    [GeneratedRegex(@"\b(result|outcome|as a result|finally|ended up|achieved|increased|decreased|reduced|improved|won|delivered|completed|learned|learnt|impact)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResultCue();
    [GeneratedRegex(@"\b(because|therefore|however|first|second|then|for example|for instance|trade-?off|so that|which means|on the other hand)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningCue();
    [GeneratedRegex(@"\d")]
    private static partial Regex Digit();
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    public Task<EvaluatorOutput> EvaluateAsync(EvaluatorInput input, AiUsage usage, CancellationToken ct)
    {
        usage.Model = Version;
        var output = new EvaluatorOutput();
        var weak = new List<(EvaluatorAnswer Answer, RubricDimension Dim, int Score)>();

        foreach (var answer in input.Answers)
        {
            var scored = new AnswerScore { Seq = answer.Seq };
            foreach (var dim in input.Rubric.Dimensions)
            {
                int score = ScoreDimension(dim.Key, answer);
                scored.Scores.Add(new DimensionScore { Dimension = dim.Key, Score = score, Evidence = BestSentence(answer.Text) });
                weak.Add((answer, dim, score));
            }
            output.Answers.Add(scored);
        }

        // Top three: weakest distinct dimensions, each tied to the answer that showed the gap.
        output.Improvements = weak
            .GroupBy(w => w.Dim.Key)
            .Select(g => g.OrderBy(w => w.Score).ThenBy(w => w.Answer.Seq).First())
            .OrderBy(w => w.Score).ThenByDescending(w => w.Dim.Weight)
            .Take(3)
            .Select(w => BuildImprovement(w.Answer, w.Dim))
            .ToList();

        var best = input.Answers.OrderByDescending(a => input.Rubric.Dimensions.Sum(d => ScoreDimension(d.Key, a))).FirstOrDefault();
        if (best is not null)
            output.Strengths.Add(new Strength { Seq = best.Seq, Quote = BestSentence(best.Text), Why = "This was your most concrete moment: it gives the interviewer something specific to believe." });

        double mean = output.Answers.SelectMany(a => a.Scores).Select(s => s.Score).DefaultIfEmpty(1).Average();
        output.Summary = mean >= 4 ? "Strong, specific answers. Sharpen the details below to stand out further."
            : mean >= 3 ? "A solid base. Your answers have the right shape but need more specifics and clearer outcomes."
            : "Your answers are short on specifics. Focus on one real example per question, with what you did and what changed.";
        return Task.FromResult(output);
    }

    public static int ScoreDimension(string key, EvaluatorAnswer a)
    {
        var t = a.Text;
        int words = DeliveryCalculator.CountWords(t);
        bool situation = SituationCue().IsMatch(t), action = ActionCue().IsMatch(t), result = ResultCue().IsMatch(t);
        bool personal = PersonalAction().IsMatch(t), cliche = Cliche().IsMatch(t);
        bool number = Digit().IsMatch(t);
        int reasoning = ReasoningCue().Matches(t).Count;
        double fillerRate = words == 0 ? 0 : (double)DeliveryCalculator.CountFillers(t) / words;

        int depth = words switch { < 15 => 1, < 40 => 2, < 80 => 3, < 180 => 4, _ => 5 };
        if (words < 8) return 1;

        double raw = key switch
        {
            "structure" or "star_structure" => 1 + (situation ? 1.2 : 0) + (action ? 1.3 : 0) + (result ? 1.5 : 0),
            "specificity" or "evidence" or "examples" => 1 + (number ? 1.5 : 0) + (action || personal ? 1 : 0) + (situation ? 0.5 : 0) + (words >= 50 ? 1 : 0),
            "ownership" => 1 + (action ? 2 : 0) + (Regex.IsMatch(t, @"\bI\b") ? 1 : 0) + (words >= 50 ? 1 : 0),
            "impact" or "result" => 1 + (result ? 2 : 0) + (number ? 2 : 0),
            "reasoning" or "depth" or "accuracy" => 1 + Math.Min(reasoning, 3) * 0.8 + (words >= 40 ? 1 : 0) + (words >= 100 ? 0.6 : 0),
            "clarity" or "communication" => depth - (fillerRate > 0.03 ? 1 : 0) - (fillerRate > 0.08 ? 1 : 0) + (reasoning > 0 ? 0.5 : 0),
            "motivation" or "fit" => cliche ? 1 : 1 + (MotivationCue().IsMatch(t) ? 1.5 : 0) + (situation ? 1 : 0) + (words >= 50 ? 1 : 0),
            "self_awareness" => cliche ? 1 : 1 + (ReflectionCue().IsMatch(t) ? 2 : 0) + (personal ? 0.5 : 0) + (words >= 50 ? 0.5 : 0),
            _ => depth,
        };
        return Math.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero), 1, 5);
    }

    private static Improvement BuildImprovement(EvaluatorAnswer a, RubricDimension d)
    {
        var quote = BestSentence(a.Text);
        return new Improvement
        {
            Skill = d.Name,
            Title = $"Improve: {d.Name}",
            Seq = a.Seq,
            Quote = quote,
            Why = d.Description,
            Rewrite = $"\"{Trim(quote, 90)} {RewriteHint(d.Key)}\"",
            Drills =
            [
                $"Re-answer aloud in 90 seconds: \"{Trim(LastSentence(a.Question), 120)}\"",
                $"Write three bullet points that would earn top marks for {d.Name}: {d.Levels.GetValueOrDefault("5", d.Description)}",
            ],
        };
    }

    private static string RewriteHint(string key) => key switch
    {
        "star_structure" => "The situation was [context], my task was [goal], so I [action], and the result was [outcome].",
        "ownership" => "My own part was [the decision you made], and I personally [the action you took].",
        "impact" or "result" => "As a result, [what changed], which I measured as [number or comparison].",
        "reasoning" => "I considered [another option], but chose this because [your reason].",
        "accuracy" => "To be precise, [the exact definition], except when [an edge case].",
        "depth" => "It works this way because [the mechanism], and the trade-off is [the cost].",
        "examples" => "For example, in [your project] I used this to [what it achieved].",
        "communication" or "clarity" => "In short, [your main point]. First, [step one]. Then, [step two].",
        "motivation" or "fit" => "This matters to me because [a personal reason], and it fits this role because [a link to the role].",
        "self_awareness" => "Honestly, I still struggle with [a real gap], so I am [what you are doing about it].",
        _ => "Specifically, I [the action you took], which led to [a result, with a number if you can].",
    };

    private static string LastSentence(string text) => SentenceSplit().Split(text.Trim()).Last();

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string BestSentence(string text)
    {
        var sentences = SentenceSplit().Split(text.Trim()).Where(s => s.Length > 0).ToList();
        var pick = sentences.OrderByDescending(s => (Digit().IsMatch(s) ? 2 : 0) + (ActionCue().IsMatch(s) ? 2 : 0) + (s.Length > 30 ? 1 : 0)).FirstOrDefault() ?? text;
        return pick.Length <= 240 ? pick : pick[..240];
    }
}
