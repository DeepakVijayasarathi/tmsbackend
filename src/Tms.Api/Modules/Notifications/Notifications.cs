using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Gamification;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Notifications;

public enum NotificationKind { ClassStartingSoon, ClassLive, MilestoneDue, MilestoneOverdue, TestPending, InterviewPending, Inactive, PracticeNudge, BadgeEarned, CourseInterest }

public class Notification : Entity
{
    public Guid UserId { get; set; }
    public NotificationKind Kind { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? Link { get; set; }
    /// <summary>Unique per user, so each reminder is sent once (e.g. "class:{id}:soon").</summary>
    public required string DedupeKey { get; set; }
    public DateTime? ReadAt { get; set; }
}

/// <summary>
/// Reminder & follow-up agent. Looks at each learner's classes, milestones, tests, activity and badges and
/// raises in-app notifications for what needs attention. Idempotent: every reminder has a dedupe key.
/// </summary>
public class ReminderAgent(TmsDbContext db, MilestoneService milestones)
{
    public const int InactiveDays = 3;
    public const int PracticeNudgeDays = 7;

    public async Task<int> RunForUserAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        var sent = (await db.Notifications.Where(n => n.UserId == userId).Select(n => n.DedupeKey).ToListAsync()).ToHashSet();
        var fresh = new List<Notification>();
        void Raise(NotificationKind kind, string key, string title, string body, string? link)
        {
            if (sent.Add(key)) fresh.Add(new Notification { UserId = userId, Kind = kind, DedupeKey = key, Title = title, Body = body, Link = link });
        }

        var enrollments = await db.Enrollments.Include(e => e.Course)
            .Where(e => e.UserId == userId && e.Status == EnrollmentStatus.Active).ToListAsync();
        var courseIds = enrollments.Select(e => e.CourseId).ToList();

        // Live classes: starting within the hour, or live now (learners and hosts).
        var soon = now.AddMinutes(60);
        var classes = await db.ClassSessions.AsNoTracking().Include(s => s.Course)
            .Where(s => (courseIds.Contains(s.CourseId) || s.InstructorId == userId || s.Course!.InstructorId == userId)
                && (s.Status == SessionStatus.Live || (s.Status == SessionStatus.Scheduled && s.StartsAt > now && s.StartsAt <= soon)))
            .ToListAsync();
        foreach (var c in classes)
        {
            var hosting = c.InstructorId == userId || c.Course!.InstructorId == userId;
            if (c.Status == SessionStatus.Live)
                Raise(NotificationKind.ClassLive, $"class:{c.Id}:live", hosting ? "Your class is live" : "A live class has started",
                    $"{c.Title} ({c.Course!.Title}) is in progress.{(hosting ? "" : " Join now — joining marks you present.")}", $"/classes/{c.Id}/room");
            else
                Raise(NotificationKind.ClassStartingSoon, $"class:{c.Id}:soon", hosting ? "Your class starts soon" : "Class starting soon",
                    $"{c.Title} starts in {Math.Max(1, (int)Math.Round((c.StartsAt - now).TotalMinutes))} minutes. {(hosting ? "You can open the room up to an hour early." : "The room opens 15 minutes before.")}", $"/classes/{c.Id}/room");
        }

        foreach (var e in enrollments)
        {
            var course = e.Course!.Title;
            // Milestones due in the next 2 days, or overdue.
            foreach (var m in (await milestones.ProgressAsync(e)).Where(m => !m.Achieved && m.DueAt != null))
            {
                if (m.DueAt < now)
                    Raise(NotificationKind.MilestoneOverdue, $"ms:{e.Id}:{m.Id}:overdue", "Milestone overdue",
                        $"“{m.Title}” in {course} was due {m.DueAt:d MMM}. You can still catch up — {m.Current}/{m.Total} done.", $"/learn/{e.Id}");
                else if (m.DueAt <= now.AddDays(2))
                    Raise(NotificationKind.MilestoneDue, $"ms:{e.Id}:{m.Id}:due", "Milestone due soon",
                        $"“{m.Title}” in {course} is due {m.DueAt:d MMM}. {m.Current}/{m.Total} done so far.", $"/learn/{e.Id}");
            }

            // Lessons finished but tests still waiting.
            var lessonTotal = await db.Lessons.CountAsync(l => l.Module!.CourseVersionId == e.CourseVersionId);
            var lessonsDone = await db.LessonProgress.CountAsync(p => p.EnrollmentId == e.Id);
            if (lessonTotal > 0 && lessonsDone >= lessonTotal)
            {
                var pending = await db.Assessments.AsNoTracking()
                    .Where(a => a.CourseVersionId == e.CourseVersionId && a.Questions.Any(q => q.Status == QuestionStatus.Approved)
                        && !db.AssessmentAttempts.Any(t => t.AssessmentId == a.Id && t.EnrollmentId == e.Id && t.Passed)
                        && db.AssessmentAttempts.Count(t => t.AssessmentId == a.Id && t.EnrollmentId == e.Id && t.Status == AttemptStatus.Submitted) < a.MaxAttempts)
                    .Select(a => new { a.Id, a.Title }).ToListAsync();
                foreach (var a in pending)
                    Raise(NotificationKind.TestPending, $"test:{e.Id}:{a.Id}", "Ready for your assessment",
                        $"You've finished every lesson in {course}. Take “{a.Title}” to work towards your certificate.", $"/learn/{e.Id}");

                var version = await db.CourseVersions.AsNoTracking().FirstAsync(v => v.Id == e.CourseVersionId);
                if (version.VivaEnabled && !await db.VivaSessions.AnyAsync(v => v.EnrollmentId == e.Id && (v.Passed || v.Status == VivaStatus.InProgress)))
                    Raise(NotificationKind.InterviewPending, $"viva:{e.Id}", "Your AI interview is ready",
                        $"Finish {course} with a short conversation with the AI interviewer.", $"/viva/{e.Id}");
            }
        }

        // Follow-ups on inactivity: one nudge per ISO week.
        if (enrollments.Count > 0)
        {
            var lastActive = await db.XpEvents.Where(x => x.UserId == userId).MaxAsync(x => (DateTime?)x.OccurredAt)
                ?? await db.LessonProgress.Where(p => db.Enrollments.Any(e => e.Id == p.EnrollmentId && e.UserId == userId)).MaxAsync(p => (DateTime?)p.CompletedAt);
            var firstEnrolled = enrollments.Min(e => e.CreatedAt);
            var week = $"{ISOWeek.GetYear(now)}-{ISOWeek.GetWeekOfYear(now)}";
            if ((lastActive ?? firstEnrolled) < now.AddDays(-InactiveDays) && firstEnrolled < now.AddDays(-InactiveDays))
            {
                var next = enrollments.OrderByDescending(e => e.CreatedAt).First();
                Raise(NotificationKind.Inactive, $"inactive:{week}", "We miss you!",
                    $"It's been a few days. Pick up {next.Course!.Title} where you left off — even 10 minutes keeps your streak going.", $"/learn/{next.Id}");
            }

            var lastPractice = await db.PracticeRounds.Where(r => r.UserId == userId && r.SubmittedAt != null).MaxAsync(r => (DateTime?)r.SubmittedAt);
            if ((lastPractice ?? firstEnrolled) < now.AddDays(-PracticeNudgeDays) || (lastPractice is null && firstEnrolled < now.AddDays(-1)))
            {
                var e = enrollments.First();
                Raise(NotificationKind.PracticeNudge, $"practice:{week}", "Quick practice round?",
                    $"Five AI questions on {e.Course!.Title} — earn XP and keep your skills sharp.", $"/practice/{e.Id}");
            }
        }

        // Celebrate badges.
        var badges = await db.UserBadges.AsNoTracking().Where(b => b.UserId == userId).ToListAsync();
        foreach (var b in badges)
        {
            var def = BadgeCatalog.All.FirstOrDefault(d => d.Key == b.BadgeKey);
            if (def is not null)
                Raise(NotificationKind.BadgeEarned, $"badge:{b.BadgeKey}", $"Badge unlocked: {def.Name}", def.Description, "/progress");
        }

        db.Notifications.AddRange(fresh);
        await db.SaveChangesAsync();
        return fresh.Count;
    }
}

/// <summary>Runs the reminder agent for every active learner on a schedule.</summary>
public class ReminderWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Reminders:IntervalMinutes", 5)));
        if (!config.GetValue("Reminders:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                var agent = scope.ServiceProvider.GetRequiredService<ReminderAgent>();
                var cutoff = DateTime.UtcNow.AddDays(-60);
                var users = await db.Enrollments.Where(e => e.Status == EnrollmentStatus.Active && e.CreatedAt > cutoff).Select(e => e.UserId)
                    .Union(db.ClassSessions.Where(s => s.StartsAt > DateTime.UtcNow.AddHours(-3)).Select(s => s.InstructorId))
                    .Distinct().ToListAsync(stoppingToken);
                var total = 0;
                foreach (var u in users) total += await agent.RunForUserAsync(u);
                if (total > 0) log.LogInformation("Reminder agent raised {Count} notifications for {Users} users", total, users.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Reminder agent run failed"); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}

public static class NotificationEndpoints
{
    public static void MapNotifications(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/me/notifications").WithTags("Notifications").RequireAuthorization();

        g.MapGet("", async (CurrentUser me, TmsDbContext db, ReminderAgent agent) =>
        {
            var userId = me.Id;
            await agent.RunForUserAsync(userId); // fresh reminders without waiting for the next scheduled run
            var items = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.CreatedAt).Take(40)
                .Select(n => new { n.Id, n.Kind, n.Title, n.Body, n.Link, n.CreatedAt, Read = n.ReadAt != null }).ToListAsync();
            return new { Unread = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null), Items = items };
        });

        g.MapPost("/{id:guid}/read", async (Guid id, CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId) ?? throw AppException.NotFound("Notification");
            n.ReadAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/read-all", async (CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var now = DateTime.UtcNow;
            await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ForEachAsync(n => n.ReadAt = now);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
