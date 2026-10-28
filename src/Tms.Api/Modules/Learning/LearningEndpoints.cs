using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Classroom;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Learning;

public record CreateSessionRequest(string Title, DateTime StartsAt, DateTime EndsAt, string? Location, string? MeetingUrl, int Capacity, string? Description = null);
public record MarkAttendanceRequest(Guid UserId, AttendanceStatus Status);

public static class LearningEndpoints
{
    public static void MapLearning(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Learning").RequireAuthorization();

        g.MapPost("/courses/{courseId:guid}/enrollments", async (Guid courseId, CurrentUser me, TmsDbContext db, EnrollmentService enrollments) =>
        {
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == courseId && c.Status == CourseStatus.Published)
                ?? throw AppException.NotFound("Course");
            if (course.Visibility == CourseVisibility.TenantOnly && !await me.IsMemberOfAsync(course.TenantId))
                throw AppException.NotFound("Course");
            if (course.PriceMinor > 0) throw new AppException(StatusCodes.Status402PaymentRequired, "This course must be purchased.");
            var enrollment = await enrollments.EnrolAsync(me.Id, course, EnrollmentSource.Free);
            await db.SaveChangesAsync();
            return new { enrollment.Id };
        });

        g.MapGet("/me/enrollments", async (CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            return await db.Enrollments.AsNoTracking()
                .Where(e => e.UserId == userId && e.Status != EnrollmentStatus.Revoked)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => new
                {
                    e.Id, e.Status, e.CreatedAt, e.CompletedAt,
                    Course = new { e.Course!.Id, e.Course.Slug, e.Course.Title, e.Course.Category, Instructor = e.Course.Instructor!.FullName },
                    LessonsTotal = db.Lessons.Count(l => db.CourseModules.Any(m => m.Id == l.ModuleId && m.CourseVersionId == e.CourseVersionId)),
                    LessonsCompleted = db.LessonProgress.Count(p => p.EnrollmentId == e.Id),
                    CertificateCode = db.Certificates.Where(c => c.EnrollmentId == e.Id && c.Status == CertificateStatus.Active)
                        .Select(c => c.CredentialCode).FirstOrDefault(),
                })
                .ToListAsync();
        });

        g.MapGet("/enrollments/{enrollmentId:guid}", async (Guid enrollmentId, CurrentUser me, TmsDbContext db,
            EnrollmentService enrollments, CertificateService certificates, MilestoneService milestones) =>
        {
            var e = await enrollments.GetOwnActiveAsync(enrollmentId, me.Id);
            var modules = await db.CourseModules.AsNoTracking().Where(m => m.CourseVersionId == e.CourseVersionId).OrderBy(m => m.Order)
                .Select(m => new
                {
                    m.Id, m.Title, m.Week, m.Objectives,
                    Lessons = m.Lessons.OrderBy(l => l.Order)
                        .Select(l => new { l.Id, l.Title, l.Kind, l.Content, l.MediaUrl, l.DurationMinutes }).ToList(),
                }).ToListAsync();
            var completed = await db.LessonProgress.Where(p => p.EnrollmentId == e.Id).Select(p => p.LessonId).ToListAsync();
            var assessments = await db.Assessments.AsNoTracking().Where(a => a.CourseVersionId == e.CourseVersionId)
                .Select(a => new
                {
                    a.Id, a.Title, a.PassMarkPercent, a.TimeLimitMinutes, a.MaxAttempts,
                    Questions = a.Questions.Count(q => q.Status == QuestionStatus.Approved),
                    AttemptsUsed = db.AssessmentAttempts.Count(t => t.AssessmentId == a.Id && t.EnrollmentId == e.Id && t.Status == AttemptStatus.Submitted),
                    BestScore = db.AssessmentAttempts.Where(t => t.AssessmentId == a.Id && t.EnrollmentId == e.Id && t.Status == AttemptStatus.Submitted)
                        .Max(t => (int?)t.ScorePercent),
                    Passed = db.AssessmentAttempts.Any(t => t.AssessmentId == a.Id && t.EnrollmentId == e.Id && t.Passed),
                    InProgressAttemptId = db.AssessmentAttempts.Where(t => t.AssessmentId == a.Id && t.EnrollmentId == e.Id && t.Status == AttemptStatus.InProgress)
                        .Select(t => (Guid?)t.Id).FirstOrDefault(),
                }).ToListAsync();
            var certificate = await db.Certificates.AsNoTracking()
                .Where(c => c.EnrollmentId == e.Id && c.Status == CertificateStatus.Active).Select(c => new { c.Id, c.CredentialCode }).FirstOrDefaultAsync();

            return new
            {
                e.Id, e.Status,
                Course = new { e.Course!.Id, e.Course.Slug, e.Course.Title },
                Modules = modules,
                CompletedLessonIds = completed,
                Assessments = assessments,
                Eligibility = await certificates.EvaluateAsync(e),
                Certificate = certificate,
                e.CreatedAt,
                Syllabus = await db.CourseVersions.AsNoTracking().Where(v => v.Id == e.CourseVersionId)
                    .Select(v => new { v.LearningOutcomes, v.Prerequisites }).FirstAsync(),
                Milestones = await milestones.ProgressAsync(e),
            };
        });

        g.MapPut("/enrollments/{enrollmentId:guid}/lessons/{lessonId:guid}/progress", async (Guid enrollmentId, Guid lessonId,
            CurrentUser me, TmsDbContext db, EnrollmentService enrollments, CertificateService certificates) =>
        {
            var e = await enrollments.GetOwnActiveAsync(enrollmentId, me.Id);
            var inVersion = await db.Lessons.AnyAsync(l => l.Id == lessonId && l.Module!.CourseVersionId == e.CourseVersionId);
            if (!inVersion) throw AppException.NotFound("Lesson");
            if (!await db.LessonProgress.AnyAsync(p => p.EnrollmentId == e.Id && p.LessonId == lessonId))
            {
                db.LessonProgress.Add(new LessonProgress { EnrollmentId = e.Id, LessonId = lessonId });
                await db.SaveChangesAsync();
            }
            var cert = await certificates.TryIssueAsync(e);
            return new { completed = true, certificateCode = cert?.CredentialCode };
        });

        // Classroom sessions
        g.MapGet("/courses/{courseId:guid}/sessions", async (Guid courseId, CurrentUser me, TmsDbContext db, CourseAuthoring authoring) =>
        {
            var course = await db.Courses.FindAsync(courseId) ?? throw AppException.NotFound("Course");
            var userId = me.Id;
            var canEdit = await authoring.CanEditAsync(course);
            if (!canEdit && !await db.Enrollments.AnyAsync(e => e.CourseId == courseId && e.UserId == userId && e.Status != EnrollmentStatus.Revoked))
                throw AppException.Forbidden("Enrol in the course to see its sessions.");
            return await db.ClassSessions.AsNoTracking().Where(s => s.CourseId == courseId).OrderBy(s => s.StartsAt)
                .Select(s => new
                {
                    s.Id, s.Title, s.Description, s.StartsAt, s.EndsAt, s.Location, s.MeetingUrl, s.Capacity, s.Status,
                    Registered = s.Attendance.Count,
                    Present = s.Attendance.Count(a => a.Status == AttendanceStatus.Present),
                    MyStatus = s.Attendance.Where(a => a.UserId == userId).Select(a => (AttendanceStatus?)a.Status).FirstOrDefault(),
                }).ToListAsync();
        });

        g.MapPost("/courses/{courseId:guid}/sessions", async (Guid courseId, CreateSessionRequest req, CurrentUser me, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            if (string.IsNullOrWhiteSpace(req.Title)) throw AppException.Invalid("Give the class a title.");
            if (req.EndsAt <= req.StartsAt) throw AppException.Invalid("The session must end after it starts.");
            var session = new ClassSession
            {
                CourseId = course.Id,
                Title = req.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                MeetingRoom = JitsiTokenService.NewRoomName(req.Title),
                StartsAt = req.StartsAt.ToUniversalTime(),
                EndsAt = req.EndsAt.ToUniversalTime(),
                Location = req.Location,
                MeetingUrl = req.MeetingUrl,
                Capacity = Math.Max(1, req.Capacity),
                InstructorId = me.Id,
            };
            db.ClassSessions.Add(session);
            await db.SaveChangesAsync();
            return new { session.Id };
        });

        g.MapPost("/sessions/{sessionId:guid}/register", async (Guid sessionId, CurrentUser me, TmsDbContext db) =>
        {
            var session = await db.ClassSessions.Include(s => s.Attendance).FirstOrDefaultAsync(s => s.Id == sessionId) ?? throw AppException.NotFound("Session");
            var userId = me.Id;
            if (!await db.Enrollments.AnyAsync(e => e.CourseId == session.CourseId && e.UserId == userId && e.Status != EnrollmentStatus.Revoked))
                throw AppException.Forbidden("Enrol in the course first.");
            if (session.Attendance.Any(a => a.UserId == userId)) return Results.NoContent();
            if (session.Attendance.Count >= session.Capacity) throw AppException.Conflict("This session is full.");
            db.SessionAttendance.Add(new SessionAttendance { SessionId = sessionId, UserId = userId, Status = AttendanceStatus.Registered });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/sessions/{sessionId:guid}/attendance", async (Guid sessionId, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var session = await db.ClassSessions.FindAsync(sessionId) ?? throw AppException.NotFound("Session");
            await authoring.GetEditableAsync(session.CourseId);
            return await db.SessionAttendance.AsNoTracking().Where(a => a.SessionId == sessionId)
                .Select(a => new { a.UserId, a.User!.FullName, a.User.Email, a.Status, a.JoinedAt, a.LeftAt }).ToListAsync();
        });

        g.MapPost("/sessions/{sessionId:guid}/attendance", async (Guid sessionId, MarkAttendanceRequest req, CurrentUser me,
            CourseAuthoring authoring, TmsDbContext db, CertificateService certificates) =>
        {
            var session = await db.ClassSessions.FindAsync(sessionId) ?? throw AppException.NotFound("Session");
            await authoring.GetEditableAsync(session.CourseId);
            var record = await db.SessionAttendance.FirstOrDefaultAsync(a => a.SessionId == sessionId && a.UserId == req.UserId)
                ?? throw AppException.NotFound("Registration");
            record.Status = req.Status;
            Audit.Record(db, me.Id, "attendance.marked", nameof(SessionAttendance), record.Id, new { req.Status });
            await db.SaveChangesAsync();

            var enrollment = await db.Enrollments.FirstOrDefaultAsync(e => e.CourseId == session.CourseId && e.UserId == req.UserId && e.Status == EnrollmentStatus.Active);
            if (enrollment is not null) await certificates.TryIssueAsync(enrollment);
            return Results.NoContent();
        });
    }
}
