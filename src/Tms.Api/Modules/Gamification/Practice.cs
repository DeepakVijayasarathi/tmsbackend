using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Ai;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Gamification;

public enum PracticeDifficulty { Easy, Medium, Hard }

/// <summary>
/// A gamified AI practice round: five questions generated from the course's own lessons. Answers and the
/// answer key stay on the server; each answer is checked as it's given for instant feedback, combos and XP.
/// Practice never affects graded assessments or certificates.
/// </summary>
public class PracticeRound : Entity
{
    public Guid UserId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid CourseId { get; set; }
    public PracticeDifficulty Difficulty { get; set; }
    /// <summary>"gemini" or "bank" (approved course questions, used when AI is unavailable).</summary>
    public string Source { get; set; } = "gemini";
    public string QuestionsJson { get; set; } = "[]";
    /// <summary>Chosen option per question, -1 when unanswered.</summary>
    public string AnswersJson { get; set; } = "[]";
    public int Correct { get; set; }
    public int Total { get; set; }
    public int Combo { get; set; }
    public int BestCombo { get; set; }
    public int XpEarned { get; set; }
    public DateTime? SubmittedAt { get; set; }
}

public record PracticeQuestion(string Prompt, List<string> Options, int CorrectIndex, string Explanation, string Lesson);
public record AnswerRequest(int Index, int Option);

public static class PracticeEndpoints
{
    private const int RoundSize = 5;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string System = """
        You write practice quiz questions for a learner on TMS Learning, using ONLY the lesson material provided.
        Write exactly 5 single-answer multiple-choice questions at the requested difficulty:
        - easy: recall of a key fact or definition,
        - medium: applying an idea to a short scenario,
        - hard: choosing between plausible approaches or spotting a subtle mistake.
        Each question has exactly 4 options, one correct, with plausible distractors of similar length.
        The explanation (one or two sentences) says why the answer is right. "lesson" is the lesson title it tests.
        Vary which option position is correct. Never use "all of the above".
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> Schema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "questions" }),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            questions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    required = new[] { "prompt", "options", "correctIndex", "explanation", "lesson" },
                    properties = new
                    {
                        prompt = new { type = "string" },
                        options = new { type = "array", items = new { type = "string" } },
                        correctIndex = new { type = "integer" },
                        explanation = new { type = "string" },
                        lesson = new { type = "string" },
                    },
                },
            },
        }),
    };

    private sealed record AiSet(List<PracticeQuestion> Questions);

    public static int PointsFor(PracticeDifficulty d, int combo) => 10 + d switch { PracticeDifficulty.Hard => 10, PracticeDifficulty.Medium => 5, _ => 0 } + (combo >= 3 ? 5 : 0);

    public static void MapPractice(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/practice").WithTags("Practice").RequireAuthorization();

        g.MapPost("/{enrollmentId:guid}/rounds", async (Guid enrollmentId, CurrentUser me, TmsDbContext db, EnrollmentService enrollments,
            ILlmClient llm, ILogger<PracticeRound> log, CancellationToken ct) =>
        {
            var e = await enrollments.GetOwnActiveAsync(enrollmentId, me.Id);
            var last = await db.PracticeRounds.Where(r => r.EnrollmentId == e.Id && r.SubmittedAt != null).OrderByDescending(r => r.SubmittedAt).FirstOrDefaultAsync(ct);
            // Adaptive difficulty: step up after a strong round, down after a weak one.
            var difficulty = last is null ? PracticeDifficulty.Easy
                : last.Total > 0 && last.Correct * 5 >= last.Total * 4 ? (PracticeDifficulty)Math.Min((int)last.Difficulty + 1, 2)
                : last.Total > 0 && last.Correct * 2 < last.Total ? (PracticeDifficulty)Math.Max((int)last.Difficulty - 1, 0)
                : last.Difficulty;

            List<PracticeQuestion>? questions = null;
            var source = "gemini";
            if (llm.IsConfigured)
            {
                try { questions = await GenerateAsync(db, e, difficulty, llm, ct); }
                catch (Exception ex) when (ex is AppException or JsonException) { log.LogWarning(ex, "AI practice generation failed; using the question bank"); }
            }
            if (questions is null || questions.Count == 0)
            {
                questions = await FromBankAsync(db, e);
                source = "bank";
            }
            if (questions.Count == 0) throw AppException.Conflict("There's nothing to practise yet — this course has no lesson material or questions.");

            var round = new PracticeRound
            {
                UserId = me.Id, EnrollmentId = e.Id, CourseId = e.CourseId, Difficulty = difficulty, Source = source,
                QuestionsJson = JsonSerializer.Serialize(questions, Web),
                AnswersJson = JsonSerializer.Serialize(Enumerable.Repeat(-1, questions.Count)),
                Total = questions.Count,
            };
            db.PracticeRounds.Add(round);
            await db.SaveChangesAsync(ct);
            return new
            {
                round.Id, round.Difficulty, round.Source, round.Total,
                Questions = questions.Select(q => new { q.Prompt, q.Options, q.Lesson }), // no answer key
            };
        });

        g.MapPost("/rounds/{roundId:guid}/answers", async (Guid roundId, AnswerRequest req, CurrentUser me, TmsDbContext db) =>
        {
            var round = await OwnOpenAsync(db, roundId, me.Id);
            var questions = JsonSerializer.Deserialize<List<PracticeQuestion>>(round.QuestionsJson, Web)!;
            var answers = JsonSerializer.Deserialize<List<int>>(round.AnswersJson)!;
            if (req.Index < 0 || req.Index >= questions.Count) throw AppException.Invalid("No such question.");
            if (answers[req.Index] >= 0) throw AppException.Conflict("You've already answered this question.");
            var q = questions[req.Index];
            if (req.Option < 0 || req.Option >= q.Options.Count) throw AppException.Invalid("Choose one of the options.");

            answers[req.Index] = req.Option;
            var correct = req.Option == q.CorrectIndex;
            var points = 0;
            if (correct)
            {
                round.Correct++;
                round.Combo++;
                round.BestCombo = Math.Max(round.BestCombo, round.Combo);
                points = PointsFor(round.Difficulty, round.Combo);
                round.XpEarned += points;
            }
            else round.Combo = 0;
            round.AnswersJson = JsonSerializer.Serialize(answers);
            await db.SaveChangesAsync();
            return new { Correct = correct, q.CorrectIndex, q.Explanation, Points = points, round.Combo, round.XpEarned };
        });

        g.MapPost("/rounds/{roundId:guid}/finish", async (Guid roundId, CurrentUser me, TmsDbContext db, GamificationService game) =>
        {
            var round = await OwnOpenAsync(db, roundId, me.Id);
            var answered = JsonSerializer.Deserialize<List<int>>(round.AnswersJson)!.Count(a => a >= 0);
            var perfect = round.Total > 0 && round.Correct == round.Total;
            if (perfect) round.XpEarned += 25;
            round.SubmittedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var levelBefore = GamificationService.LevelFor(await db.XpEvents.Where(x => x.UserId == me.Id).SumAsync(x => (int?)x.Points) ?? 0);
            var newBadges = await game.SyncAsync(me.Id);
            var xp = await db.XpEvents.Where(x => x.UserId == me.Id).SumAsync(x => (int?)x.Points) ?? 0;
            var level = GamificationService.LevelFor(xp);
            return new
            {
                round.Correct, round.Total, Answered = answered, round.BestCombo, round.XpEarned, round.Difficulty,
                Perfect = perfect,
                NextDifficulty = round.Correct * 5 >= round.Total * 4 && round.Difficulty < PracticeDifficulty.Hard ? "up"
                    : round.Correct * 2 < round.Total && round.Difficulty > PracticeDifficulty.Easy ? "down" : "same",
                TotalXp = xp,
                Level = level,
                LeveledUp = level.Number > levelBefore.Number,
                NewBadges = newBadges.Select(b => new { b.Key, b.Name, b.Description, b.Icon, b.Tier }),
            };
        });

        g.MapGet("/{enrollmentId:guid}/history", async (Guid enrollmentId, CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            return await db.PracticeRounds.AsNoTracking().Where(r => r.EnrollmentId == enrollmentId && r.UserId == userId && r.SubmittedAt != null)
                .OrderByDescending(r => r.SubmittedAt).Take(10)
                .Select(r => new { r.Id, r.Difficulty, r.Correct, r.Total, r.BestCombo, r.XpEarned, r.SubmittedAt }).ToListAsync();
        });
    }

    private static async Task<PracticeRound> OwnOpenAsync(TmsDbContext db, Guid roundId, Guid userId)
    {
        var round = await db.PracticeRounds.FirstOrDefaultAsync(r => r.Id == roundId && r.UserId == userId) ?? throw AppException.NotFound("Practice round");
        if (round.SubmittedAt is not null) throw AppException.Conflict("This round is finished. Start a new one.");
        return round;
    }

    private static async Task<List<PracticeQuestion>> GenerateAsync(TmsDbContext db, Enrollment e, PracticeDifficulty difficulty, ILlmClient llm, CancellationToken ct)
    {
        var lessons = await db.Lessons.AsNoTracking().Where(l => l.Module!.CourseVersionId == e.CourseVersionId && l.Content != "")
            .Select(l => new { l.Title, l.Content }).ToListAsync(ct);
        if (lessons.Count == 0) return [];
        // A random selection of lessons each round keeps questions varied; the budget keeps the prompt compact.
        var budget = 7000;
        var material = new List<object>();
        foreach (var l in lessons.OrderBy(_ => Random.Shared.Next()))
        {
            var text = l.Content.Length > 1500 ? l.Content[..1500] : l.Content;
            if (budget - text.Length < 0 && material.Count > 0) break;
            budget -= text.Length;
            material.Add(new { lesson = l.Title, text });
        }
        var prompt = JsonSerializer.Serialize(new { difficulty = difficulty.ToString().ToLowerInvariant(), material });
        var reply = await llm.CompleteAsync(System, [new ChatTurn("user", prompt)], Schema, ct);
        if (reply.Refused) return [];
        var set = JsonSerializer.Deserialize<AiSet>(reply.Text, Web);
        return (set?.Questions ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q.Prompt) && q.Options is { Count: 4 } && q.CorrectIndex is >= 0 and < 4 && q.Options.All(o => !string.IsNullOrWhiteSpace(o)))
            .Take(RoundSize).ToList();
    }

    /// <summary>Approved single-answer questions from the course's own assessments, shuffled.</summary>
    private static async Task<List<PracticeQuestion>> FromBankAsync(TmsDbContext db, Enrollment e)
    {
        var questions = await db.Questions.AsNoTracking().Include(q => q.Options)
            .Where(q => q.Status == QuestionStatus.Approved && q.Kind != QuestionKind.MultipleChoice
                && db.Assessments.Any(a => a.Id == q.AssessmentId && a.CourseVersionId == e.CourseVersionId))
            .ToListAsync();
        return questions.OrderBy(_ => Random.Shared.Next()).Take(RoundSize).Select(q =>
        {
            var options = q.Options.OrderBy(o => o.Order).ToList();
            return new PracticeQuestion(q.Prompt, options.Select(o => o.Text).ToList(), options.FindIndex(o => o.IsCorrect), q.Explanation ?? "", "Course question bank");
        }).Where(q => q.CorrectIndex >= 0).ToList();
    }
}
