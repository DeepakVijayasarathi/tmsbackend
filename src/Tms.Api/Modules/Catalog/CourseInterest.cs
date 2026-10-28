using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Notifications;

namespace Tms.Api.Modules.Catalog;

public enum InterestStatus { New, Contacted, Enrolled, Closed }

/// <summary>
/// A lead: someone opened a shared course link and said they're interested through the chat form.
/// Visitors don't need an account; the instructor follows up from the course editor.
/// </summary>
public class CourseInterest : Entity
{
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    /// <summary>Set when the visitor was signed in.</summary>
    public Guid? UserId { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public string? Phone { get; set; }
    /// <summary>What they want from the course, e.g. "Get a job".</summary>
    public string? Goal { get; set; }
    /// <summary>When they'd like to learn, e.g. "Weekends".</summary>
    public string? PreferredTime { get; set; }
    public string? Message { get; set; }
    /// <summary>Referral code or campaign tag from the shared link, if any.</summary>
    public string? Source { get; set; }
    public InterestStatus Status { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public record CourseInterestRequest(string? Name, string? Email, string? Phone, string? Goal, string? PreferredTime, string? Message, string? Source);
public record InterestStatusRequest(InterestStatus Status);

public static class CourseInterestEndpoints
{
    public static void MapCourseInterest(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Catalog");

        // Public: anyone with the course link can register interest. Rate limited per IP.
        g.MapPost("/courses/{courseId:guid}/interest", async (Guid courseId, CourseInterestRequest req, CurrentUser me, TmsDbContext db) =>
        {
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == courseId && c.Status == CourseStatus.Published)
                ?? throw AppException.NotFound("Course");

            var name = Clip(req.Name, 120) ?? throw AppException.Invalid("Tell us your name.");
            var email = Clip(req.Email, 254);
            if (email is null || !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@')) throw AppException.Invalid("Enter a valid email address.");
            var phone = Clip(req.Phone, 30);
            if (phone is not null && phone.Count(char.IsDigit) is < 7 or > 15) throw AppException.Invalid("Enter a valid phone number, or skip it.");

            // One lead per person per course: a repeat submission updates the existing one.
            var normalized = email.ToLowerInvariant();
            var lead = await db.CourseInterests.FirstOrDefaultAsync(i => i.CourseId == courseId && i.Email == normalized);
            var isNew = lead is null;
            lead ??= db.CourseInterests.Add(new CourseInterest { CourseId = courseId, Name = name, Email = normalized }).Entity;
            lead.Name = name;
            lead.Phone = phone ?? lead.Phone;
            lead.Goal = Clip(req.Goal, 120) ?? lead.Goal;
            lead.PreferredTime = Clip(req.PreferredTime, 120) ?? lead.PreferredTime;
            lead.Message = Clip(req.Message, 1000) ?? lead.Message;
            lead.Source = Clip(req.Source, 60) ?? lead.Source;
            lead.UserId = me.IdOrNull ?? lead.UserId;
            lead.UpdatedAt = DateTime.UtcNow;
            if (!isNew && lead.Status == InterestStatus.Closed) lead.Status = InterestStatus.New;

            if (isNew)
                db.Notifications.Add(new Notification
                {
                    UserId = course.InstructorId,
                    Kind = NotificationKind.CourseInterest,
                    DedupeKey = $"interest:{lead.Id}",
                    Title = "New interest in your course",
                    Body = $"{name} is interested in {course.Title}.",
                    Link = $"/teach/courses/{course.Id}?tab=interest",
                });
            await db.SaveChangesAsync();
            return new { received = true, isNew };
        }).RequireRateLimiting("interest");

        g.MapGet("/courses/{courseId:guid}/interests", async (Guid courseId, TmsDbContext db, CourseAuthoring authoring) =>
        {
            await authoring.GetEditableAsync(courseId);
            return await db.CourseInterests.AsNoTracking().Where(i => i.CourseId == courseId)
                .OrderByDescending(i => i.UpdatedAt)
                .Select(i => new { i.Id, i.Name, i.Email, i.Phone, i.Goal, i.PreferredTime, i.Message, i.Source, i.Status, i.CreatedAt, i.UpdatedAt, HasAccount = i.UserId != null })
                .ToListAsync();
        }).RequireAuthorization();

        g.MapPatch("/course-interests/{id:guid}", async (Guid id, InterestStatusRequest req, TmsDbContext db, CourseAuthoring authoring) =>
        {
            var lead = await db.CourseInterests.FirstOrDefaultAsync(i => i.Id == id) ?? throw AppException.NotFound("Interest");
            await authoring.GetEditableAsync(lead.CourseId);
            lead.Status = req.Status;
            lead.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string? Clip(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }
}
