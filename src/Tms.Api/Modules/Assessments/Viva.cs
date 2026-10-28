using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Ai;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Assessments;

public enum VivaStatus { InProgress, Completed, Abandoned }

/// <summary>
/// AI interviewer (viva): a conversational graded test. The agent asks questions grounded in the course's
/// lessons, probes with follow-ups, then scores the whole transcript against a rubric. The transcript and
/// scoring live on the server; the learner only ever sends answers.
/// </summary>
public class VivaSession : Entity
{
    public Guid UserId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid CourseVersionId { get; set; }
    public VivaStatus Status { get; set; }
    public string TranscriptJson { get; set; } = "[]";
    public int MainQuestions { get; set; }
    public int MaxQuestions { get; set; }
    public int PassMarkPercent { get; set; }
    public int? ScorePercent { get; set; }
    public bool Passed { get; set; }
    public string? ResultJson { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public record VivaTurn(string Role, string Text, bool FollowUp = false);
public record VivaAnswer(string Answer);

public static class VivaEndpoints
{
    public const int MaxAttempts = 3;
    private const int MaxAnswerChars = 2000;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string InterviewerSystem = """
        You are a friendly but rigorous oral examiner (viva) for an online course on TMS Learning.
        Use ONLY the course material provided. Ask one question at a time, in plain language, no more than two sentences.
        After each learner answer, decide:
        - if the answer is vague, partly wrong or missing a key point, ask ONE short follow-up that probes that gap (followUp = true);
        - otherwise move to a new question on a different lesson (followUp = false).
        A follow-up must rephrase or narrow the question (e.g. ask for one specific missing point) — never repeat it word for word.
        Never ask more than one follow-up in a row. Never reveal the answer or say whether they were right.
        A brief neutral acknowledgement ("Thanks.", "Okay.") before the next question is fine.
        The first message greets the learner by first name in one short sentence, then asks the first question.
        """;

    private const string EvaluatorSystem = """
        You grade an oral exam (viva) transcript for an online course, using ONLY the course material as the answer key.
        Score each criterion from 0 to 10: "Understanding" (correct grasp of concepts), "Application" (applies ideas to
        situations), "Communication" (clear, structured answers). overallPercent is your overall judgement from 0 to 100.
        Give 2-3 specific strengths and 2-3 specific improvements that cite what the learner said, and a two-sentence summary
        addressed to the learner ("you"). Be fair: short but correct answers can score well.
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> TurnSchema = Schema(new
    {
        type = "object",
        required = new[] { "message", "followUp" },
        properties = new { message = new { type = "string" }, followUp = new { type = "boolean" } },
    });

    private static readonly IReadOnlyDictionary<string, JsonElement> ResultSchema = Schema(new
    {
        type = "object",
        required = new[] { "overallPercent", "criteria", "strengths", "improvements", "summary" },
        properties = new
        {
            overallPercent = new { type = "integer" },
            criteria = new
            {
                type = "array",
                items = new { type = "object", required = new[] { "name", "score", "comment" }, properties = new { name = new { type = "string" }, score = new { type = "integer" }, comment = new { type = "string" } } },
            },
            strengths = new { type = "array", items = new { type = "string" } },
            improvements = new { type = "array", items = new { type = "string" } },
            summary = new { type = "string" },
        },
    });

    private static IReadOnlyDictionary<string, JsonElement> Schema(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private sealed record AiTurn(string Message, bool FollowUp);
    public sealed record VivaCriterion(string Name, int Score, string Comment);
    public sealed record VivaResult(int OverallPercent, List<VivaCriterion> Criteria, List<string> Strengths, List<string> Improvements, string Summary);

    public static void MapViva(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/viva").WithTags("Viva").RequireAuthorization();

        g.MapGet("/{enrollmentId:guid}", async (Guid enrollmentId, CurrentUser me, TmsDbContext db, EnrollmentService enrollments) =>
        {
            var e = await enrollments.GetOwnActiveAsync(enrollmentId, me.Id);
            var version = await db.CourseVersions.AsNoTracking().FirstAsync(v => v.Id == e.CourseVersionId);
            var sessions = await db.VivaSessions.AsNoTracking().Where(s => s.EnrollmentId == e.Id).OrderByDescending(s => s.CreatedAt).ToListAsync();
            var open = sessions.FirstOrDefault(s => s.Status == VivaStatus.InProgress);
            return new
            {
                Enabled = version.VivaEnabled,
                version.VivaPassMarkPercent,
                version.VivaQuestionCount,
                MaxAttempts,
                AttemptsUsed = sessions.Count(s => s.Status == VivaStatus.Completed),
                Passed = sessions.Any(s => s.Passed),
                BestScore = sessions.Max(s => s.ScorePercent),
                Open = open is null ? null : View(open),
                History = sessions.Where(s => s.Status == VivaStatus.Completed).Select(s => new { s.Id, s.ScorePercent, s.Passed, s.CompletedAt }),
            };
        });

        g.MapPost("/{enrollmentId:guid}/sessions", async (Guid enrollmentId, CurrentUser me, TmsDbContext db, EnrollmentService enrollments, ILlmClient llm, CancellationToken ct) =>
        {
            var e = await enrollments.GetOwnActiveAsync(enrollmentId, me.Id);
            var version = await db.CourseVersions.AsNoTracking().FirstAsync(v => v.Id == e.CourseVersionId, ct);
            if (!version.VivaEnabled) throw AppException.Conflict("This course doesn't have an AI interview.");
            if (!llm.IsConfigured) throw new AppException(StatusCodes.Status503ServiceUnavailable, "The AI interviewer isn't configured on this server.");

            var existing = await db.VivaSessions.FirstOrDefaultAsync(s => s.EnrollmentId == e.Id && s.Status == VivaStatus.InProgress, ct);
            if (existing is not null) return View(existing);
            if (await db.VivaSessions.AnyAsync(s => s.EnrollmentId == e.Id && s.Passed, ct)) throw AppException.Conflict("You've already passed this interview.");
            if (await db.VivaSessions.CountAsync(s => s.EnrollmentId == e.Id && s.Status == VivaStatus.Completed, ct) >= MaxAttempts)
                throw AppException.Conflict($"You've used all {MaxAttempts} interview attempts.");

            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.Id, ct);
            var session = new VivaSession
            {
                UserId = me.Id, EnrollmentId = e.Id, CourseVersionId = version.Id, Status = VivaStatus.InProgress,
                MaxQuestions = Math.Clamp(version.VivaQuestionCount, 2, 8), PassMarkPercent = version.VivaPassMarkPercent,
            };
            var material = await MaterialAsync(db, version.Id, ct);
            var first = await AskAsync(llm, material, [], user.FullName, session.MaxQuestions, ct);
            session.MainQuestions = 1;
            session.TranscriptJson = JsonSerializer.Serialize(new List<VivaTurn> { new("interviewer", first.Message) }, Web);
            db.VivaSessions.Add(session);
            await db.SaveChangesAsync(ct);
            return View(session);
        });

        g.MapPost("/sessions/{sessionId:guid}/answers", async (Guid sessionId, VivaAnswer req, CurrentUser me, TmsDbContext db, ILlmClient llm,
            CertificateService certificates, CancellationToken ct) =>
        {
            var session = await db.VivaSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == me.Id, ct) ?? throw AppException.NotFound("Interview");
            if (session.Status != VivaStatus.InProgress) throw AppException.Conflict("This interview is over.");
            var transcript = JsonSerializer.Deserialize<List<VivaTurn>>(session.TranscriptJson, Web)!;
            // A transcript ending with the learner means scoring failed last time: retry it without a new answer.
            var pendingScore = transcript.LastOrDefault()?.Role == "learner";
            if (!pendingScore)
            {
                var answer = (req.Answer ?? "").Trim();
                if (answer.Length == 0) throw AppException.Invalid("Type an answer first.");
                if (answer.Length > MaxAnswerChars) answer = answer[..MaxAnswerChars];
                transcript.Add(new VivaTurn("learner", answer));
            }
            var material = await MaterialAsync(db, session.CourseVersionId, ct);
            var lastWasFollowUp = transcript.LastOrDefault(t => t.Role == "interviewer")?.FollowUp == true;

            // Finished once the last main question (and any follow-up to it) has been answered.
            if (pendingScore || (session.MainQuestions >= session.MaxQuestions && (lastWasFollowUp || !await WantsFollowUpAsync(llm, material, transcript, ct))))
            {
                session.TranscriptJson = JsonSerializer.Serialize(transcript, Web);
                await db.SaveChangesAsync(ct); // keep the answers even if scoring fails
                var result = await EvaluateAsync(llm, material, transcript, ct);
                session.Status = VivaStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;
                session.ScorePercent = Math.Clamp(result.OverallPercent, 0, 100);
                session.Passed = session.ScorePercent >= session.PassMarkPercent;
                session.ResultJson = JsonSerializer.Serialize(result, Web);
                Audit.Record(db, me.Id, "viva.completed", nameof(VivaSession), session.Id, new { session.ScorePercent, session.Passed });
                await db.SaveChangesAsync(ct);
                var enrollment = await db.Enrollments.FirstAsync(x => x.Id == session.EnrollmentId, ct);
                if (session.Passed && enrollment.Status == EnrollmentStatus.Active) await certificates.TryIssueAsync(enrollment);
                return View(session);
            }

            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.Id, ct);
            var next = await AskAsync(llm, material, transcript, user.FullName, session.MaxQuestions, ct, forbidFollowUp: lastWasFollowUp);
            var followUp = next.FollowUp && !lastWasFollowUp;
            if (!followUp) session.MainQuestions++;
            transcript.Add(new VivaTurn("interviewer", next.Message, followUp));
            session.TranscriptJson = JsonSerializer.Serialize(transcript, Web);
            await db.SaveChangesAsync(ct);
            return View(session);
        });

        g.MapPost("/sessions/{sessionId:guid}/abandon", async (Guid sessionId, CurrentUser me, TmsDbContext db) =>
        {
            var session = await db.VivaSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == me.Id) ?? throw AppException.NotFound("Interview");
            if (session.Status == VivaStatus.InProgress) { session.Status = VivaStatus.Abandoned; await db.SaveChangesAsync(); }
            return Results.NoContent();
        });
    }

    private static object View(VivaSession s) => new
    {
        s.Id, s.Status, s.MainQuestions, s.MaxQuestions, s.PassMarkPercent, s.ScorePercent, s.Passed, s.CompletedAt,
        Transcript = JsonSerializer.Deserialize<List<VivaTurn>>(s.TranscriptJson, Web),
        Result = s.ResultJson is null ? null : JsonSerializer.Deserialize<VivaResult>(s.ResultJson, Web),
    };

    private static async Task<string> MaterialAsync(TmsDbContext db, Guid versionId, CancellationToken ct)
    {
        var lessons = await db.Lessons.AsNoTracking().Where(l => l.Module!.CourseVersionId == versionId)
            .OrderBy(l => l.Module!.Order).ThenBy(l => l.Order).Select(l => new { l.Title, l.Content }).ToListAsync(ct);
        return JsonSerializer.Serialize(lessons.Select(l => new { lesson = l.Title, text = l.Content.Length > 1800 ? l.Content[..1800] : l.Content }));
    }

    private static string TranscriptText(IEnumerable<VivaTurn> t) =>
        string.Join("\n", t.Select(x => $"{(x.Role == "interviewer" ? "Interviewer" : "Learner")}: {x.Text}"));

    private static async Task<AiTurn> AskAsync(ILlmClient llm, string material, List<VivaTurn> transcript, string fullName, int maxQuestions, CancellationToken ct, bool forbidFollowUp = false)
    {
        var first = fullName.Split(' ').FirstOrDefault(w => !w.EndsWith('.')) ?? fullName;
        var prompt = $"""
            COURSE MATERIAL (JSON): {material}
            Learner's first name: {first}. Planned main questions: {maxQuestions}.
            {(transcript.Count == 0 ? "Start the interview." : "TRANSCRIPT SO FAR:\n" + TranscriptText(transcript))}
            {(forbidFollowUp ? "Your previous question was a follow-up, so now ask a NEW main question (followUp = false)." : "")}
            """;
        var reply = await llm.CompleteAsync(InterviewerSystem, [new ChatTurn("user", prompt)], TurnSchema, ct);
        if (reply.Refused) throw new AppException(StatusCodes.Status422UnprocessableEntity, "The AI interviewer couldn't continue. Try again.");
        try
        {
            var turn = JsonSerializer.Deserialize<AiTurn>(reply.Text, Web);
            if (turn is null || string.IsNullOrWhiteSpace(turn.Message)) throw new JsonException();
            return turn with { Message = turn.Message.Trim() };
        }
        catch (JsonException) { throw new AppException(StatusCodes.Status502BadGateway, "The AI interviewer returned an unreadable reply. Try again."); }
    }

    private static async Task<bool> WantsFollowUpAsync(ILlmClient llm, string material, List<VivaTurn> transcript, CancellationToken ct)
    {
        try
        {
            var turn = JsonSerializer.Deserialize<AiTurn>((await llm.CompleteAsync(InterviewerSystem,
                [new ChatTurn("user", $"COURSE MATERIAL (JSON): {material}\nTRANSCRIPT SO FAR:\n{TranscriptText(transcript)}\nThis was the final main question. Only set followUp = true if the last answer clearly needs one probe.")], TurnSchema, ct)).Text, Web);
            return turn?.FollowUp == true;
        }
        catch (Exception ex) when (ex is JsonException or AppException) { return false; }
    }

    private static async Task<VivaResult> EvaluateAsync(ILlmClient llm, string material, List<VivaTurn> transcript, CancellationToken ct)
    {
        var reply = await llm.CompleteAsync(EvaluatorSystem, [new ChatTurn("user", $"COURSE MATERIAL (JSON): {material}\n\nTRANSCRIPT:\n{TranscriptText(transcript)}")], ResultSchema, ct);
        try
        {
            var r = JsonSerializer.Deserialize<VivaResult>(reply.Text, Web) ?? throw new JsonException();
            return r with { Criteria = (r.Criteria ?? []).Select(c => c with { Score = Math.Clamp(c.Score, 0, 10) }).ToList(), Strengths = r.Strengths ?? [], Improvements = r.Improvements ?? [] };
        }
        catch (JsonException) { throw new AppException(StatusCodes.Status502BadGateway, "The AI couldn't score the interview. Your answers are saved — try submitting again."); }
    }
}
