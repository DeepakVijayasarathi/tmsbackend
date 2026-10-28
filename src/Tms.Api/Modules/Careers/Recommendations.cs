using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Ai;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Careers;

public record InterestsRequest(List<string> Interests);
public record Recommendation(string Type, Guid Id, string Slug, string Title, string Subtitle, string Reason);
public record RecommendationSet(string Summary, string Source, List<Recommendation> Items);

/// <summary>
/// Course and career-path recommendations from a learner's interests and history. Gemini ranks the
/// real catalog; anything it returns that isn't in the catalog is dropped. Without an AI key (or if
/// the AI fails) a keyword match on interests is used instead, so the feature always works.
/// </summary>
public class RecommendationService(TmsDbContext db, ILlmClient llm, ILogger<RecommendationService> log)
{
    private sealed record Candidate(string Type, Guid Id, string Slug, string Title, string Subtitle, string Text);
    private sealed record AiPick(string Type, string Id, string Reason);
    private sealed record AiSet(string Summary, List<AiPick> Items);

    private const string System = """
        You are the course advisor for TMS Learning, an online learning platform with live classes and career paths
        ("Meet to Career"). Recommend at most four items from the CATALOG for this learner, best first.
        Use only ids that appear in the catalog. Prefer items matching the learner's interests; recommend a career
        path when it fits their interests. Each reason is one short sentence addressed to the learner ("you").
        The summary is one encouraging sentence about their direction. Never invent courses.
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> Schema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "summary", "items" }),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            summary = new { type = "string" },
            items = new
            {
                type = "array",
                maxItems = 4,
                items = new
                {
                    type = "object",
                    required = new[] { "type", "id", "reason" },
                    properties = new { type = new { type = "string", @enum = new[] { "course", "path" } }, id = new { type = "string" }, reason = new { type = "string" } },
                },
            },
        }),
    };

    public async Task<RecommendationSet> ForAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var interests = SplitInterests(user.Interests);
        var enrolledIds = await db.Enrollments.Where(e => e.UserId == userId && e.Status != EnrollmentStatus.Revoked).Select(e => e.CourseId).ToListAsync(ct);
        var enrolledTitles = await db.Courses.Where(c => enrolledIds.Contains(c.Id)).Select(c => c.Title).ToListAsync(ct);
        var joinedPaths = await db.CareerEnrollments.Where(e => e.UserId == userId).Select(e => e.PathId).ToListAsync(ct);

        var memberOf = await db.TenantMemberships.Where(m => m.UserId == userId).Select(m => m.TenantId).ToListAsync(ct);
        var courses = await db.Courses.AsNoTracking().Include(c => c.Instructor)
            .Where(c => c.Status == CourseStatus.Published && !enrolledIds.Contains(c.Id)
                && (c.Visibility == CourseVisibility.Public || memberOf.Contains(c.TenantId)))
            .ToListAsync(ct);
        var paths = await db.CareerPaths.AsNoTracking().Where(p => p.IsPublished && !joinedPaths.Contains(p.Id)).ToListAsync(ct);
        var candidates = courses.Select(c => new Candidate("course", c.Id, c.Slug, c.Title, $"{c.Category} · {c.Level}", $"{c.Title} {c.Category} {c.Summary}"))
            .Concat(paths.Select(p => new Candidate("path", p.Id, p.Slug, p.Title, $"Career path · {p.Role}", $"{p.Title} {p.Role} {p.Summary} {p.Skills}")))
            .ToList();
        if (candidates.Count == 0) return new RecommendationSet("You're enrolled in everything available — nice work.", "rules", []);

        if (llm.IsConfigured && interests.Count > 0)
        {
            try
            {
                var prompt = JsonSerializer.Serialize(new
                {
                    learner = new { interests, enrolledCourses = enrolledTitles },
                    catalog = candidates.Select(c => new { c.Type, id = c.Id, c.Title, details = c.Subtitle, about = c.Text }),
                });
                var reply = await llm.CompleteAsync(System, [new ChatTurn("user", prompt)], Schema, ct);
                var set = JsonSerializer.Deserialize<AiSet>(reply.Text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var items = (set?.Items ?? [])
                    .Select(i => (i, c: candidates.FirstOrDefault(c => c.Type == i.Type && c.Id.ToString().Equals(i.Id, StringComparison.OrdinalIgnoreCase))))
                    .Where(x => x.c is not null)
                    .DistinctBy(x => x.c!.Id)
                    .Take(4)
                    .Select(x => new Recommendation(x.c!.Type, x.c.Id, x.c.Slug, x.c.Title, x.c.Subtitle, x.i.Reason.Trim()))
                    .ToList();
                if (items.Count > 0) return new RecommendationSet(set!.Summary.Trim(), "gemini", items);
            }
            catch (Exception ex) when (ex is AppException or JsonException or NullReferenceException)
            {
                log.LogWarning(ex, "AI recommendations failed; using interest matching");
            }
        }
        return Rules(interests, candidates);
    }

    private static RecommendationSet Rules(List<string> interests, List<Candidate> candidates)
    {
        var words = interests.SelectMany(i => i.ToLowerInvariant().Split([' ', '&', '/', ',', '-'], StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 3).Distinct().ToList();
        var scored = candidates
            .Select(c => (c, hits: words.Where(w => c.Text.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList()))
            .OrderByDescending(x => x.hits.Count).ThenBy(x => x.c.Type == "path" ? 0 : 1)
            .Take(4)
            .Select(x => new Recommendation(x.c.Type, x.c.Id, x.c.Slug, x.c.Title, x.c.Subtitle,
                x.hits.Count > 0 ? $"Matches your interest in {interests.First(i => x.hits.Any(h => i.Contains(h, StringComparison.OrdinalIgnoreCase)))}." : "Popular with learners on TMS."))
            .ToList();
        var summary = interests.Count > 0 ? $"Picked for your interests: {string.Join(", ", interests.Take(3))}." : "Tell us your interests to get personal recommendations.";
        return new RecommendationSet(summary, "rules", scored);
    }

    public static List<string> SplitInterests(string? value) =>
        (value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public static class InterestEndpoints
{
    public static void MapInterests(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me").WithTags("Careers").RequireAuthorization();

        me.MapPut("/interests", async (InterestsRequest req, CurrentUser current, TmsDbContext db) =>
        {
            var clean = req.Interests.Select(i => i.Trim().Replace("|", "/")).Where(i => i.Length is > 0 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            var user = await db.Users.FirstAsync(u => u.Id == current.Id);
            user.Interests = string.Join("|", clean);
            await db.SaveChangesAsync();
            return new { interests = clean };
        });

        me.MapPost("/interests/chat", async (InterestChatRequest req, CurrentUser current, InterestChatService chat, CancellationToken ct) =>
            await chat.NextAsync(current.Id, req.Messages, ct));

        me.MapGet("/recommendations", async (CurrentUser current, RecommendationService recs, CancellationToken ct) =>
            await recs.ForAsync(current.Id, ct));
    }
}
