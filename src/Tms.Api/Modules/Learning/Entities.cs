using Tms.Api.Common;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Learning;

public enum EnrollmentSource { Free, Purchase, Assigned }
public enum EnrollmentStatus { Active, Completed, Revoked }
public enum AttendanceStatus { Registered, Present, Absent }
public enum SessionStatus { Scheduled, Live, Ended, Cancelled }

public class Enrollment : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public Guid CourseVersionId { get; set; }
    public CourseVersion? CourseVersion { get; set; }
    public EnrollmentSource Source { get; set; }
    public EnrollmentStatus Status { get; set; }
    public Guid? OrderId { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class LessonProgress : Entity
{
    public Guid EnrollmentId { get; set; }
    public Guid LessonId { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public class ClassSession : Entity
{
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Location { get; set; }
    public string? MeetingUrl { get; set; }
    /// <summary>Jitsi room name for the built-in virtual classroom. Random suffix so rooms can't be guessed.</summary>
    public string MeetingRoom { get; set; } = "";
    public SessionStatus Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int Capacity { get; set; }
    public Guid InstructorId { get; set; }
    public List<SessionAttendance> Attendance { get; set; } = [];
}

public class SessionAttendance : Entity
{
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public AttendanceStatus Status { get; set; }
    /// <summary>First time the learner entered the virtual classroom.</summary>
    public DateTime? JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
}
