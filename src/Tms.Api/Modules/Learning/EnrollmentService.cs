using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Learning;

public class EnrollmentService(TmsDbContext db)
{
    /// <summary>Enrols a learner against the course's current published version. Idempotent.</summary>
    public async Task<Enrollment> EnrolAsync(Guid userId, Course course, EnrollmentSource source, Guid? orderId = null)
    {
        if (course.PublishedVersionId is not { } versionId) throw AppException.Invalid("Course has not been published.");

        var existing = await db.Enrollments.FirstOrDefaultAsync(e => e.UserId == userId && e.CourseId == course.Id)
            ?? db.Enrollments.Local.FirstOrDefault(e => e.UserId == userId && e.CourseId == course.Id);
        if (existing is not null)
        {
            if (existing.Status != EnrollmentStatus.Revoked) return existing;
            existing.Status = EnrollmentStatus.Active;
            existing.Source = source;
            existing.OrderId = orderId;
            existing.CourseVersionId = versionId;
            return existing;
        }

        var enrollment = new Enrollment
        {
            UserId = userId,
            CourseId = course.Id,
            CourseVersionId = versionId,
            Source = source,
            Status = EnrollmentStatus.Active,
            OrderId = orderId,
        };
        db.Enrollments.Add(enrollment);
        Audit.Record(db, userId, "enrollment.created", nameof(Enrollment), enrollment.Id, new { source }, course.TenantId);
        return enrollment;
    }

    public async Task<Enrollment> GetOwnActiveAsync(Guid enrollmentId, Guid userId)
    {
        var e = await db.Enrollments.Include(x => x.Course).FirstOrDefaultAsync(x => x.Id == enrollmentId && x.UserId == userId)
            ?? throw AppException.NotFound("Enrollment");
        if (e.Status == EnrollmentStatus.Revoked) throw AppException.Forbidden("This enrollment is no longer active.");
        return e;
    }

    public Task<List<Guid>> LessonIdsAsync(Guid versionId) =>
        db.Lessons.Where(l => db.CourseModules.Any(m => m.Id == l.ModuleId && m.CourseVersionId == versionId))
            .Select(l => l.Id).ToListAsync();
}
