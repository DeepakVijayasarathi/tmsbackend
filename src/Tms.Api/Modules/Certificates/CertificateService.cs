using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Careers;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Certificates;

public record Eligibility(
    int LessonsCompleted, int LessonsTotal,
    int AssessmentsPassed, int AssessmentsTotal,
    int SessionsAttended, int SessionsRequired,
    int? BestScorePercent,
    bool VivaRequired = false,
    bool VivaPassed = false)
{
    public bool Eligible => LessonsCompleted >= LessonsTotal && AssessmentsPassed >= AssessmentsTotal && SessionsAttended >= SessionsRequired
        && (!VivaRequired || VivaPassed);
}

public class CertificateService(TmsDbContext db, EnrollmentService enrollments, ReferralRelease referrals, CareerService careers)
{
    /// <summary>Eligibility is computed only from server-side records: progress, graded attempts, attendance.</summary>
    public async Task<Eligibility> EvaluateAsync(Enrollment enrollment)
    {
        var version = await db.CourseVersions.FirstAsync(v => v.Id == enrollment.CourseVersionId);
        var lessonIds = await enrollments.LessonIdsAsync(version.Id);
        var done = await db.LessonProgress.CountAsync(p => p.EnrollmentId == enrollment.Id && lessonIds.Contains(p.LessonId));

        var assessmentIds = await db.Assessments
            .Where(a => a.CourseVersionId == version.Id && db.Questions.Any(q => q.AssessmentId == a.Id && q.Status == QuestionStatus.Approved))
            .Select(a => a.Id).ToListAsync();
        var passedAttempts = await db.AssessmentAttempts
            .Where(t => t.EnrollmentId == enrollment.Id && t.Passed && assessmentIds.Contains(t.AssessmentId))
            .Select(t => new { t.AssessmentId, t.ScorePercent }).ToListAsync();

        var attended = await db.SessionAttendance.CountAsync(a => a.UserId == enrollment.UserId && a.Status == AttendanceStatus.Present
            && db.ClassSessions.Any(s => s.Id == a.SessionId && s.CourseId == enrollment.CourseId));

        return new Eligibility(done, lessonIds.Count,
            passedAttempts.Select(p => p.AssessmentId).Distinct().Count(), assessmentIds.Count,
            attended, version.MinSessionsAttended,
            passedAttempts.Count == 0 ? null : passedAttempts.Max(p => p.ScorePercent),
            VivaRequired: version.VivaEnabled,
            VivaPassed: version.VivaEnabled && await db.VivaSessions.AnyAsync(v => v.EnrollmentId == enrollment.Id && v.Passed));
    }

    /// <summary>
    /// Runs after every learner progress event (lesson completed, assessment passed, class attended):
    /// issues the certificate when eligible, releases staged referral commission, and advances career paths.
    /// </summary>
    public async Task<Certificate?> TryIssueAsync(Enrollment enrollment)
    {
        var cert = await IssueIfEligibleAsync(enrollment);
        await referrals.ReleaseAsync(enrollment);
        await careers.RefreshAsync(enrollment.UserId);
        return cert;
    }

    private async Task<Certificate?> IssueIfEligibleAsync(Enrollment enrollment)
    {
        var existing = await db.Certificates.FirstOrDefaultAsync(c => c.EnrollmentId == enrollment.Id && c.Status == CertificateStatus.Active);
        if (existing is not null) return existing;

        var eligibility = await EvaluateAsync(enrollment);
        if (!eligibility.Eligible) return null;

        var course = await db.Courses.Include(c => c.Tenant).FirstAsync(c => c.Id == enrollment.CourseId);
        var version = await db.CourseVersions.FirstAsync(v => v.Id == enrollment.CourseVersionId);
        var user = await db.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var now = DateTime.UtcNow;

        var cert = new Certificate
        {
            CredentialCode = $"TMS-{Text.RandomCode(4)}-{Text.RandomCode(4)}-{Text.RandomCode(4)}",
            UserId = user.Id,
            CourseId = course.Id,
            CourseVersionId = version.Id,
            EnrollmentId = enrollment.Id,
            IssuerTenantId = course.TenantId,
            RecipientName = user.FullName,
            CourseTitle = course.Title,
            IssuerName = course.Tenant!.Name,
            ScorePercent = eligibility.BestScorePercent,
            IssuedAt = now,
            ExpiresAt = version.CertificateValidityMonths is { } months ? now.AddMonths(months) : null,
            Status = CertificateStatus.Active,
        };
        db.Certificates.Add(cert);
        db.CertificateEvents.Add(new CertificateEvent { CertificateId = cert.Id, Type = CertificateEventType.Issued, ActorUserId = user.Id });
        enrollment.Status = EnrollmentStatus.Completed;
        enrollment.CompletedAt = now;
        Audit.Record(db, user.Id, "certificate.issued", nameof(Certificate), cert.Id, new { cert.CredentialCode }, course.TenantId);
        await db.SaveChangesAsync();
        return cert;
    }
}
