using System.Text.RegularExpressions;

namespace Tms.Api.Modules.Ai;

public record LessonDoc(Guid LessonId, string LessonTitle, string ModuleTitle, string Content);
public record Chunk(Guid LessonId, string LessonTitle, string ModuleTitle, string Text, double Score);

/// <summary>
/// Lexical retrieval over the lessons a learner is entitled to (BM25-style scoring on paragraph
/// chunks). The <see cref="IContentRetriever"/> seam lets a vector index replace it without touching callers.
/// </summary>
public interface IContentRetriever
{
    IReadOnlyList<Chunk> Retrieve(IEnumerable<LessonDoc> lessons, string query, int take);
}

public partial class LexicalRetriever : IContentRetriever
{
    private const int ChunkChars = 900;
    private static readonly HashSet<string> Stop =
    [
        "the", "and", "for", "with", "that", "this", "what", "how", "why", "are", "was", "you", "your", "can", "does",
        "from", "into", "about", "when", "which", "will", "would", "should", "could", "have", "has", "had", "not", "but",
        "explain", "tell", "please", "mean", "means", "is", "it", "of", "to", "in", "a", "an", "on", "or", "be", "do", "i", "me",
    ];

    public IReadOnlyList<Chunk> Retrieve(IEnumerable<LessonDoc> lessons, string query, int take)
    {
        var chunks = lessons.SelectMany(Split).ToList();
        if (chunks.Count == 0) return [];

        var terms = Tokens(query).Distinct().ToList();
        var docs = chunks.Select(c => Tokens($"{c.LessonTitle} {c.ModuleTitle} {c.Text}").ToList()).ToList();
        var avgLen = docs.Average(d => d.Count);
        const double k1 = 1.4, b = 0.75;

        var scored = chunks.Select((c, i) =>
        {
            var doc = docs[i];
            double score = 0;
            foreach (var t in terms)
            {
                var tf = doc.Count(w => w == t);
                if (tf == 0) continue;
                var df = docs.Count(d => d.Contains(t));
                var idf = Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
                score += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * doc.Count / avgLen));
            }
            return c with { Score = score };
        }).ToList();

        var hits = scored.Where(c => c.Score > 0).OrderByDescending(c => c.Score).Take(take).ToList();
        // Nothing matched lexically (e.g. "summarise this course"): fall back to the opening of each lesson.
        return hits.Count > 0 ? hits : scored.GroupBy(c => c.LessonId).Select(g => g.First()).Take(take).ToList();
    }

    private static IEnumerable<Chunk> Split(LessonDoc lesson)
    {
        var buffer = "";
        foreach (var para in lesson.Content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (buffer.Length + para.Length > ChunkChars && buffer.Length > 0)
            {
                yield return new Chunk(lesson.LessonId, lesson.LessonTitle, lesson.ModuleTitle, buffer, 0);
                buffer = "";
            }
            buffer = buffer.Length == 0 ? para : $"{buffer}\n\n{para}";
        }
        if (buffer.Length > 0) yield return new Chunk(lesson.LessonId, lesson.LessonTitle, lesson.ModuleTitle, buffer, 0);
    }

    private static IEnumerable<string> Tokens(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 1 && !Stop.Contains(w)).Select(Stem);

    private static string Stem(string w) =>
        w.Length > 5 && w.EndsWith("ing") ? w[..^3]
        : w.Length > 4 && w.EndsWith("es") ? w[..^2]
        : w.Length > 3 && w.EndsWith('s') ? w[..^1]
        : w;

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();
}
