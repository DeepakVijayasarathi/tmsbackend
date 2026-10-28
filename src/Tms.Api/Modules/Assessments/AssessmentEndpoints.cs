using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Assessments;

public record SubmittedAnswer(Guid QuestionId, List<Guid> OptionIds);
public record SubmitAttemptRequest(List<SubmittedAnswer> Answers);

public static class AssessmentEndpoints
{
    /// <summary>Late submissions inside this window still count, to absorb network latency.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    public static void MapAssessments(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Assessments").RequireAuthorization();

        g.MapPost("/assessments/{assessmentId:guid}/attempts", async (Guid assessmentId, CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var assessment = await db.Assessments.Include(a => a.Questions.Where(q => q.Status == QuestionStatus.Approved))
                .FirstOrDefaultAsync(a => a.Id == assessmentId) ?? throw AppException.NotFound("Assessment");
            var enrollment = await db.Enrollments.FirstOrDefaultAsync(e => e.UserId == userId && e.CourseVersionId == assessment.CourseVersionId
                && e.Status != EnrollmentStatus.Revoked) ?? throw AppException.Forbidden("You are not enrolled in this course.");
            if (assessment.Questions.Count == 0) throw AppException.Invalid("This assessment has no questions yet.");

            var attempts = await db.AssessmentAttempts.Where(t => t.AssessmentId == assessmentId && t.EnrollmentId == enrollment.Id).ToListAsync();
            var open = attempts.FirstOrDefault(t => t.Status == AttemptStatus.InProgress);
            if (open is not null && (open.ExpiresAt is null || open.ExpiresAt + Grace > DateTime.UtcNow))
                return await ViewAsync(db, open);
            if (open is not null) await GradeAsync(db, open, []); // expired and abandoned: close it with the answers on file

            if (attempts.Any(t => t.Passed)) throw AppException.Conflict("You have already passed this assessment.");
            if (attempts.Count(t => t.Status == AttemptStatus.Submitted) >= assessment.MaxAttempts)
                throw AppException.Conflict("No attempts remaining.");

            var questions = assessment.ShuffleQuestions
                ? assessment.Questions.OrderBy(_ => Random.Shared.Next()).ToList()
                : assessment.Questions.OrderBy(q => q.Order).ToList();
            var attempt = new AssessmentAttempt
            {
                AssessmentId = assessmentId,
                EnrollmentId = enrollment.Id,
                UserId = userId,
                Status = AttemptStatus.InProgress,
                ExpiresAt = assessment.TimeLimitMinutes is { } m ? DateTime.UtcNow.AddMinutes(m) : null,
                QuestionOrder = string.Join(',', questions.Select(q => q.Id)),
                MaxPoints = questions.Sum(q => q.Points),
            };
            db.AssessmentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            return await ViewAsync(db, attempt);
        });

        g.MapGet("/attempts/{attemptId:guid}", async (Guid attemptId, CurrentUser me, TmsDbContext db) =>
        {
            var attempt = await OwnAttemptAsync(db, attemptId, me.Id);
            return await ViewAsync(db, attempt);
        });

        g.MapPost("/attempts/{attemptId:guid}/submit", async (Guid attemptId, SubmitAttemptRequest req, CurrentUser me,
            TmsDbContext db, CertificateService certificates) =>
        {
            var attempt = await OwnAttemptAsync(db, attemptId, me.Id);
            if (attempt.Status == AttemptStatus.Submitted) throw AppException.Conflict("This attempt was already submitted.");
            var late = attempt.ExpiresAt is { } exp && DateTime.UtcNow > exp + Grace;
            await GradeAsync(db, attempt, late ? [] : req.Answers ?? []);

            var enrollment = await db.Enrollments.FirstAsync(e => e.Id == attempt.EnrollmentId);
            var cert = attempt.Passed ? await certificates.TryIssueAsync(enrollment) : null;
            return new { attempt.Id, attempt.ScorePercent, attempt.Passed, late, certificateCode = cert?.CredentialCode };
        });

        g.MapGet("/attempts/{attemptId:guid}/result", async (Guid attemptId, CurrentUser me, TmsDbContext db) =>
        {
            var attempt = await OwnAttemptAsync(db, attemptId, me.Id);
            if (attempt.Status != AttemptStatus.Submitted) throw AppException.Conflict("Submit the attempt to see results.");
            var assessment = await db.Assessments.AsNoTracking().FirstAsync(a => a.Id == attempt.AssessmentId);
            var used = await db.AssessmentAttempts.CountAsync(t => t.AssessmentId == attempt.AssessmentId && t.EnrollmentId == attempt.EnrollmentId && t.Status == AttemptStatus.Submitted);

            // Answer keys are released only once they can no longer help on a future attempt.
            var revealKey = attempt.Passed || used >= assessment.MaxAttempts;
            var questions = await LoadOrderedQuestionsAsync(db, attempt);
            var answers = await db.AttemptAnswers.AsNoTracking().Where(a => a.AttemptId == attempt.Id).ToListAsync();

            return new
            {
                attempt.Id, assessment.Title, attempt.ScorePoints, attempt.MaxPoints, attempt.ScorePercent, attempt.Passed,
                assessment.PassMarkPercent, AttemptsUsed = used, assessment.MaxAttempts, AnswerKeyReleased = revealKey,
                Questions = questions.Select(q =>
                {
                    var answer = answers.FirstOrDefault(a => a.QuestionId == q.Id);
                    return new
                    {
                        q.Id, q.Prompt, q.Kind, q.Points,
                        Correct = answer?.IsCorrect ?? false,
                        Selected = Split(answer?.SelectedOptionIds),
                        Options = q.Options.OrderBy(o => o.Order).Select(o => new { o.Id, o.Text, IsCorrect = revealKey ? o.IsCorrect : (bool?)null }),
                        Explanation = revealKey ? q.Explanation : null,
                    };
                }),
            };
        });
    }

    private static async Task<AssessmentAttempt> OwnAttemptAsync(TmsDbContext db, Guid attemptId, Guid userId) =>
        await db.AssessmentAttempts.FirstOrDefaultAsync(t => t.Id == attemptId && t.UserId == userId) ?? throw AppException.NotFound("Attempt");

    private static async Task<List<Question>> LoadOrderedQuestionsAsync(TmsDbContext db, AssessmentAttempt attempt)
    {
        var ids = Split(attempt.QuestionOrder);
        var questions = await db.Questions.AsNoTracking().Include(q => q.Options).Where(q => ids.Contains(q.Id)).ToListAsync();
        return ids.Select(id => questions.First(q => q.Id == id)).ToList();
    }

    /// <summary>The learner's view of an attempt. Never includes which options are correct.</summary>
    private static async Task<object> ViewAsync(TmsDbContext db, AssessmentAttempt attempt)
    {
        var assessment = await db.Assessments.AsNoTracking().FirstAsync(a => a.Id == attempt.AssessmentId);
        var questions = await LoadOrderedQuestionsAsync(db, attempt);
        return new
        {
            attempt.Id, attempt.Status, attempt.ExpiresAt, assessment.Title, assessment.PassMarkPercent,
            Questions = questions.Select(q => new
            {
                q.Id, q.Prompt, q.Kind, q.Points,
                Options = q.Options.OrderBy(o => o.Order).Select(o => new { o.Id, o.Text }),
            }),
        };
    }

    private static async Task GradeAsync(TmsDbContext db, AssessmentAttempt attempt, List<SubmittedAnswer> submitted)
    {
        var assessment = await db.Assessments.FirstAsync(a => a.Id == attempt.AssessmentId);
        var questions = await LoadOrderedQuestionsAsync(db, attempt);
        var score = 0;
        foreach (var q in questions)
        {
            var selected = submitted.FirstOrDefault(s => s.QuestionId == q.Id)?.OptionIds ?? [];
            var graded = Grader.Grade(q, selected);
            score += graded.Points;
            db.AttemptAnswers.Add(new AttemptAnswer
            {
                AttemptId = attempt.Id,
                QuestionId = q.Id,
                SelectedOptionIds = string.Join(',', selected.Distinct()),
                IsCorrect = graded.IsCorrect,
                PointsAwarded = graded.Points,
            });
        }
        attempt.ScorePoints = score;
        attempt.MaxPoints = questions.Sum(q => q.Points);
        attempt.ScorePercent = Grader.Percent(score, attempt.MaxPoints);
        attempt.Passed = attempt.ScorePercent >= assessment.PassMarkPercent;
        attempt.Status = AttemptStatus.Submitted;
        attempt.SubmittedAt = DateTime.UtcNow;
        Audit.Record(db, attempt.UserId, "attempt.submitted", nameof(AssessmentAttempt), attempt.Id, new { attempt.ScorePercent, attempt.Passed });
        await db.SaveChangesAsync();
    }

    private static List<Guid> Split(string? csv) =>
        string.IsNullOrEmpty(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
}
