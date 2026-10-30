using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace Infrastructure;

public record ParsedResume(string Text, List<string> Skills);

public class UnsupportedResumeException(string message) : Exception(message);

/// <summary>Extracts text in memory only. The uploaded file is never persisted.</summary>
public static partial class ResumeParser
{
    public const int MaxChars = 12_000;

    private static readonly string[] KnownSkills =
    [
        "c#", "c++", "java", "python", "javascript", "typescript", "go", "rust", "sql", "mysql", "postgresql", "mongodb",
        "react", "angular", "node", "next.js", ".net", "spring", "django", "flask", "html", "css", "git", "docker",
        "kubernetes", "aws", "azure", "gcp", "linux", "excel", "power bi", "tableau", "machine learning", "pandas",
        "numpy", "data structures", "algorithms", "dbms", "operating systems", "computer networks", "oop", "rest",
        "communication", "leadership", "teamwork",
    ];

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public static ParsedResume Parse(string fileName, Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        buffer.Position = 0;

        string text;
        try
        {
            text = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".pdf" => ReadPdf(buffer),
                ".docx" => ReadDocx(buffer),
                ".txt" => Encoding.UTF8.GetString(buffer.ToArray()),
                _ => throw new UnsupportedResumeException("Please upload a PDF, DOCX or TXT file."),
            };
        }
        catch (Exception e) when (e is not UnsupportedResumeException)
        {
            throw new UnsupportedResumeException("We could not read that file. It may be corrupted or password protected.");
        }

        text = BlankLines().Replace(Spaces().Replace(text, " ").Replace("\r", ""), "\n\n").Trim();
        if (text.Length < 30)
            throw new UnsupportedResumeException("We could not find any text in that file. Scanned PDFs are not supported yet.");
        if (text.Length > MaxChars) text = text[..MaxChars];

        var lower = text.ToLowerInvariant();
        var skills = KnownSkills.Where(s => Regex.IsMatch(lower, $@"(?<![\w+#.]){Regex.Escape(s)}(?![\w+#])")).ToList();
        return new ParsedResume(text, skills);
    }

    private static string ReadPdf(Stream s)
    {
        using var pdf = PdfDocument.Open(s);
        return string.Join('\n', pdf.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))));
    }

    private static string ReadDocx(Stream s)
    {
        using var doc = WordprocessingDocument.Open(s, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";
        return string.Join('\n', body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText));
    }
}
