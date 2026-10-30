using System.Text.RegularExpressions;
using Domain;

namespace Infrastructure;

/// <summary>Delivery metrics are always computed in code, never by the LLM (BUILD_PLAN section 8).</summary>
public static partial class DeliveryCalculator
{
    // Deliberately excludes "like", "so" and "actually" alone: they are too often legitimate words.
    [GeneratedRegex(@"\b(um+|uh+|umm+|hmm+|er+m?|you know|i mean|sort of|kind of|basically|literally)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FillerRegex();

    [GeneratedRegex(@"[\p{L}\p{N}'’-]+")]
    private static partial Regex WordRegex();

    public static int CountWords(string text) => WordRegex().Matches(text).Count;

    public static int CountFillers(string text) => FillerRegex().Matches(text).Count;

    public static DeliveryMetric Compute(Guid sessionId, IReadOnlyList<Turn> turns)
    {
        var answers = turns.Where(t => t.Speaker == Speakers.Candidate).ToList();
        int answerWords = answers.Sum(t => CountWords(t.Text));
        int interviewerWords = turns.Where(t => t.Speaker == Speakers.Interviewer).Sum(t => CountWords(t.Text));
        int totalWords = answerWords + interviewerWords;

        var timed = answers.Where(t => t is { StartMs: not null, EndMs: not null } && t.EndMs > t.StartMs).ToList();
        double spokenSec = timed.Sum(t => (t.EndMs!.Value - t.StartMs!.Value) / 1000.0);
        int timedWords = timed.Sum(t => CountWords(t.Text));

        return new DeliveryMetric
        {
            SessionId = sessionId,
            // WPM and duration only exist for voice answers; typed answers have no speaking pace.
            Wpm = spokenSec > 0 ? (int)Math.Round(timedWords / (spokenSec / 60)) : null,
            AvgAnswerSec = timed.Count > 0 ? Math.Round(spokenSec / timed.Count, 1) : null,
            FillerCount = answers.Sum(t => CountFillers(t.Text)),
            LongPauses = answers.Sum(t => t.LongPauses),
            TalkRatio = totalWords > 0 ? Math.Round((double)answerWords / totalWords, 3) : 0,
            AvgAnswerWords = answers.Count > 0 ? Math.Round((double)answerWords / answers.Count, 1) : 0,
        };
    }
}
