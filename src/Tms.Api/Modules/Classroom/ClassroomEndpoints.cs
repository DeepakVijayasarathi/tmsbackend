using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Classroom;

/// <summary>
/// Live virtual classroom on top of course sessions. Hosts (the session's instructor or anyone who can
/// edit the course) start and end classes; enrolled learners join and are marked present automatically.
/// </summary>
public static class ClassroomEndpoints
{
    /// <summary>A host may open the room this long before the scheduled start.</summary>
    private const int HostEarlyMinutes = 60;
    /// <summary>A live class nobody ended stops being listed as upcoming this long after its scheduled end.</summary>
    private const int StaleLiveHours = 3;

    private sealed record Access(ClassSession Session, Course Course, bool IsHost, Enrollment? Enrollment);

    private static async Task<Access> ResolveAsync(Guid sessionId, CurrentUser me, TmsDbContext db, CourseAuthoring authoring)
    {
        var session = await db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId) ?? throw AppException.NotFound("Class");
        var course = await db.Courses.FirstAsync(c => c.Id == session.CourseId);
        var userId = me.Id;
        var isHost = session.InstructorId == userId || await authoring.CanEditAsync(course);
        var enrollment = await db.Enrollments.FirstOrDefaultAsync(e => e.CourseId == session.CourseId && e.UserId == userId && e.Status != EnrollmentStatus.Revoked);
        if (!isHost && enrollment is null) throw AppException.Forbidden("Enrol in the course to join its live classes.");
        return new Access(session, course, isHost, enrollment);
    }

    private static DateTime JoinOpensAt(ClassSession s, bool isHost, JitsiTokenService jitsi) =>
        s.StartsAt.AddMinutes(-(isHost ? HostEarlyMinutes : jitsi.JoinEarlyMinutes));

    /// <summary>Null when the caller may join now, otherwise the reason they can't.</summary>
    private static string? JoinBlocker(ClassSession s, bool isHost, JitsiTokenService jitsi, DateTime now)
    {
        if (s.Status == SessionStatus.Cancelled) return "This class was cancelled.";
        if (s.Status == SessionStatus.Ended) return "This class has ended.";
        if (s.Status == SessionStatus.Live) return null;
        if (now < JoinOpensAt(s, isHost, jitsi))
            return isHost
                ? $"You can start this class up to {HostEarlyMinutes} minutes before it begins."
                : $"The classroom opens {jitsi.JoinEarlyMinutes} minutes before the class starts.";
        if (!isHost && now > s.EndsAt) return "This class has ended.";
        return null;
    }

    public static void MapClassroom(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Classroom").RequireAuthorization();

        // Every class the caller teaches or is enrolled for. scope=upcoming (default) or past.
        g.MapGet("/me/sessions", async (string? scope, CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var now = DateTime.UtcNow;
            var staleLive = now.AddHours(-StaleLiveHours);
            var mine = db.ClassSessions.AsNoTracking().Where(s =>
                s.InstructorId == userId || s.Course!.InstructorId == userId ||
                db.Enrollments.Any(e => e.CourseId == s.CourseId && e.UserId == userId && e.Status != EnrollmentStatus.Revoked));

            var past = scope == "past";
            mine = past
                ? mine.Where(s => !((s.Status == SessionStatus.Live && s.EndsAt > staleLive) || (s.Status == SessionStatus.Scheduled && s.EndsAt > now)))
                    .OrderByDescending(s => s.StartsAt).Take(50)
                : mine.Where(s => (s.Status == SessionStatus.Live && s.EndsAt > staleLive) || (s.Status == SessionStatus.Scheduled && s.EndsAt > now))
                    .OrderBy(s => s.StartsAt);

            return await mine.Select(s => new
            {
                s.Id, s.Title, s.Description, s.StartsAt, s.EndsAt, s.Status, s.Location, s.Capacity,
                Course = new { s.Course!.Id, s.Course.Title, s.Course.Slug },
                Instructor = db.Users.Where(u => u.Id == s.InstructorId).Select(u => u.FullName).FirstOrDefault(),
                IsHost = s.InstructorId == userId || s.Course.InstructorId == userId,
                Registered = s.Attendance.Count,
                Present = s.Attendance.Count(a => a.Status == AttendanceStatus.Present),
                MyStatus = s.Attendance.Where(a => a.UserId == userId).Select(a => (AttendanceStatus?)a.Status).FirstOrDefault(),
            }).ToListAsync();
        });

        g.MapGet("/sessions/{sessionId:guid}", async (Guid sessionId, CurrentUser me, TmsDbContext db, CourseAuthoring authoring, JitsiTokenService jitsi) =>
        {
            var (s, course, isHost, _) = await ResolveAsync(sessionId, me, db, authoring);
            var userId = me.Id;
            var counts = await db.SessionAttendance.AsNoTracking().Where(a => a.SessionId == s.Id)
                .GroupBy(a => a.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToListAsync();
            var instructor = await db.Users.AsNoTracking().Where(u => u.Id == s.InstructorId).Select(u => u.FullName).FirstOrDefaultAsync();
            var myStatus = await db.SessionAttendance.AsNoTracking().Where(a => a.SessionId == s.Id && a.UserId == userId)
                .Select(a => (AttendanceStatus?)a.Status).FirstOrDefaultAsync();
            var blocker = JoinBlocker(s, isHost, jitsi, DateTime.UtcNow);
            return new
            {
                s.Id, s.Title, s.Description, s.StartsAt, s.EndsAt, s.Status, s.StartedAt, s.EndedAt, s.Location, s.Capacity,
                Course = new { course.Id, course.Title, course.Slug },
                Instructor = instructor,
                IsHost = isHost,
                MyStatus = myStatus,
                Registered = counts.Sum(c => c.Count),
                Present = counts.Where(c => c.Status == AttendanceStatus.Present).Sum(c => c.Count),
                CanJoin = blocker is null,
                JoinBlockedReason = blocker,
                JoinOpensAt = JoinOpensAt(s, isHost, jitsi),
            };
        });

        g.MapPost("/sessions/{sessionId:guid}/join", async (Guid sessionId, CurrentUser me, TmsDbContext db, CourseAuthoring authoring,
            JitsiTokenService jitsi, CertificateService certificates) =>
        {
            var (s, course, isHost, enrollment) = await ResolveAsync(sessionId, me, db, authoring);
            var now = DateTime.UtcNow;
            if (JoinBlocker(s, isHost, jitsi, now) is { } reason) throw AppException.Conflict(reason);
            var userId = me.Id;

            if (string.IsNullOrEmpty(s.MeetingRoom)) s.MeetingRoom = JitsiTokenService.NewRoomName(s.Title);
            if (isHost && s.Status == SessionStatus.Scheduled)
            {
                s.Status = SessionStatus.Live;
                s.StartedAt = now;
                Audit.Record(db, userId, "session.started", nameof(ClassSession), s.Id, null);
            }

            if (!isHost)
            {
                var record = await db.SessionAttendance.FirstOrDefaultAsync(a => a.SessionId == s.Id && a.UserId == userId);
                if (record is null)
                {
                    if (await db.SessionAttendance.CountAsync(a => a.SessionId == s.Id) >= s.Capacity)
                        throw AppException.Conflict("This class is full.");
                    record = new SessionAttendance { SessionId = s.Id, UserId = userId };
                    db.SessionAttendance.Add(record);
                }
                record.Status = AttendanceStatus.Present;
                record.JoinedAt ??= now;
                record.LeftAt = null;
            }
            await db.SaveChangesAsync();

            // Live attendance can complete the certificate requirements.
            if (!isHost && enrollment is { Status: EnrollmentStatus.Active }) await certificates.TryIssueAsync(enrollment);

            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
            var expires = (s.EndsAt > now ? s.EndsAt : now).AddHours(StaleLiveHours);
            return new
            {
                SessionId = s.Id,
                s.Title,
                s.StartsAt,
                s.EndsAt,
                s.Status,
                Course = new { course.Id, course.Title, course.Slug },
                Domain = jitsi.Domain,
                Room = s.MeetingRoom,
                Token = jitsi.CreateToken(s.MeetingRoom, user.Id, user.FullName, user.Email, moderator: isHost, expires),
                DisplayName = user.FullName,
                user.Email,
                IsHost = isHost,
            };
        });

        g.MapPost("/sessions/{sessionId:guid}/leave", async (Guid sessionId, CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var record = await db.SessionAttendance.FirstOrDefaultAsync(a => a.SessionId == sessionId && a.UserId == userId);
            if (record is not null)
            {
                record.LeftAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });

        g.MapPost("/sessions/{sessionId:guid}/end", async (Guid sessionId, CurrentUser me, TmsDbContext db, CourseAuthoring authoring) =>
        {
            var (s, _, isHost, _) = await ResolveAsync(sessionId, me, db, authoring);
            if (!isHost) throw AppException.Forbidden("Only the host can end the class.");
            if (s.Status is SessionStatus.Ended or SessionStatus.Cancelled) return Results.NoContent();
            var now = DateTime.UtcNow;
            s.Status = SessionStatus.Ended;
            s.StartedAt ??= now;
            s.EndedAt = now;
            // Learners who registered but never joined are absent; anyone still in the room has now left.
            foreach (var a in await db.SessionAttendance.Where(a => a.SessionId == s.Id).ToListAsync())
            {
                if (a.Status == AttendanceStatus.Registered) a.Status = AttendanceStatus.Absent;
                if (a.Status == AttendanceStatus.Present && a.LeftAt is null) a.LeftAt = now;
            }
            Audit.Record(db, me.Id, "session.ended", nameof(ClassSession), s.Id, null);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/sessions/{sessionId:guid}/cancel", async (Guid sessionId, CurrentUser me, TmsDbContext db, CourseAuthoring authoring) =>
        {
            var (s, _, isHost, _) = await ResolveAsync(sessionId, me, db, authoring);
            if (!isHost) throw AppException.Forbidden("Only the host can cancel the class.");
            if (s.Status != SessionStatus.Scheduled) throw AppException.Conflict("Only a class that hasn't started can be cancelled.");
            s.Status = SessionStatus.Cancelled;
            Audit.Record(db, me.Id, "session.cancelled", nameof(ClassSession), s.Id, null);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
