using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Careers;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Gamification;

public enum XpSource { LessonCompleted, AssessmentPassed, ClassAttended, MilestoneAchieved, CourseCompleted, CareerCompleted, Practice, InterviewPassed }

/// <summary>One XP award. (UserId, Source, RefKey) is unique, so re-deriving XP never double counts.</summary>
public class XpEvent : Entity
{
    public Guid UserId { get; set; }
    public XpSource Source { get; set; }
    public required string RefKey { get; set; }
    public int Points { get; set; }
    public required string Label { get; set; }
    /// <summary>When the underlying activity happened (drives streaks and the activity heatmap).</summary>
    public DateTime OccurredAt { get; set; }
}

public class UserBadge : Entity
{
    public Guid UserId { get; set; }
    public required string BadgeKey { get; set; }
    public DateTime AwardedAt { get; set; } = DateTime.UtcNow;
}

public record BadgeDef(string Key, string Name, string Description, string Icon, string Tier, string Metric, int Target);
public record Level(int Number, string Title, int MinXp, int? NextXp);

public static class BadgeCatalog
{
    public static readonly BadgeDef[] All =
    [
        new("first-steps", "First Steps", "Complete your first lesson.", "BookOpen", "bronze", "lessons", 1),
        new("bookworm", "Bookworm", "Complete 10 lessons.", "Library", "silver", "lessons", 10),
        new("live-learner", "Live Learner", "Attend your first live class.", "Video", "bronze", "classes", 1),
        new("front-row", "Front Row Regular", "Attend 5 live classes.", "Users", "silver", "classes", 5),
        new("test-passed", "Test Passed", "Pass your first assessment.", "ClipboardCheck", "bronze", "assessments", 1),
        new("quiz-ace", "Quiz Ace", "Score 100% on an assessment.", "Target", "gold", "perfect", 1),
        new("milestone-maker", "Milestone Maker", "Achieve 3 course milestones.", "Flag", "silver", "milestones", 3),
        new("finisher", "Course Finisher", "Earn your first certificate.", "Award", "gold", "certificates", 1),
        new("on-fire", "On Fire", "Learn 3 days in a row.", "Flame", "bronze", "streak", 3),
        new("unstoppable", "Unstoppable", "Learn 7 days in a row.", "Zap", "gold", "streak", 7),
        new("practice-pro", "Practice Pro", "Answer 25 AI practice questions correctly.", "Brain", "silver", "practice", 25),
        new("sharpshooter", "Sharpshooter", "Finish an AI practice round with a perfect score.", "Crosshair", "gold", "perfectRounds", 1),
        new("interview-ready", "Interview Ready", "Pass an AI interview.", "MessageSquare", "gold", "interviews", 1),
        new("rising-star", "Rising Star", "Earn 1,000 XP.", "Star", "silver", "xp", 1000),
        new("forward-deployed", "Forward Deployed", "Complete a Meet to Career path.", "Rocket", "platinum", "careers", 1),
    ];
}

public class GamificationService(TmsDbContext db, MilestoneService milestones)
{
    private static readonly (int Xp, string Title)[] Levels =
    [
        (0, "Explorer"), (100, "Learner"), (250, "Achiever"), (500, "Practitioner"), (900, "Specialist"),
        (1400, "Expert"), (2000, "Master"), (2800, "Champion"), (3800, "Legend"),
    ];

    public static Level LevelFor(int xp)
    {
        var i = Array.FindLastIndex(Levels, l => xp >= l.Xp);
        return new Level(i + 1, Levels[i].Title, Levels[i].Xp, i + 1 < Levels.Length ? Levels[i + 1].Xp : null);
    }

    /// <summary>Derives XP from the learner's records and awards any newly earned badges. Safe to call often.</summary>
    public async Task<List<BadgeDef>> SyncAsync(Guid userId)
    {
        var existing = (await db.XpEvents.Where(x => x.UserId == userId).Select(x => x.Source + ":" + x.RefKey).ToListAsync()).ToHashSet();
        var fresh = new List<XpEvent>();
        void Add(XpSource source, string refKey, int points, string label, DateTime at)
        {
            if (existing.Add(source + ":" + refKey))
                fresh.Add(new XpEvent { UserId = userId, Source = source, RefKey = refKey, Points = points, Label = label, OccurredAt = at });
        }

        var lessons = await db.LessonProgress.Where(p => db.Enrollments.Any(e => e.Id == p.EnrollmentId && e.UserId == userId))
            .Select(p => new { p.Id, p.CompletedAt, Title = db.Lessons.Where(l => l.Id == p.LessonId).Select(l => l.Title).FirstOrDefault() }).ToListAsync();
        foreach (var l in lessons) Add(XpSource.LessonCompleted, l.Id.ToString(), 10, $"Completed “{l.Title}”", l.CompletedAt);

        var passed = await db.AssessmentAttempts.Where(a => a.UserId == userId && a.Passed)
            .Select(a => new { a.Id, a.ScorePercent, a.SubmittedAt, a.CreatedAt, a.Assessment!.Title }).ToListAsync();
        foreach (var a in passed) Add(XpSource.AssessmentPassed, a.Id.ToString(), a.ScorePercent == 100 ? 75 : 50, $"Passed “{a.Title}” ({a.ScorePercent}%)", a.SubmittedAt ?? a.CreatedAt);

        var classes = await db.SessionAttendance.Where(a => a.UserId == userId && a.Status == AttendanceStatus.Present)
            .Select(a => new { a.Id, a.JoinedAt, a.CreatedAt, Title = db.ClassSessions.Where(s => s.Id == a.SessionId).Select(s => s.Title).FirstOrDefault() }).ToListAsync();
        foreach (var c in classes) Add(XpSource.ClassAttended, c.Id.ToString(), 30, $"Attended live class “{c.Title}”", c.JoinedAt ?? c.CreatedAt);

        var certs = await db.Certificates.Where(c => c.UserId == userId && c.Status == CertificateStatus.Active)
            .Select(c => new { c.Id, c.CourseTitle, c.IssuedAt }).ToListAsync();
        foreach (var c in certs) Add(XpSource.CourseCompleted, c.Id.ToString(), 150, $"Earned the “{c.CourseTitle}” certificate", c.IssuedAt);

        var careers = await db.CareerEnrollments.Where(c => c.UserId == userId && c.CompletedAt != null)
            .Select(c => new { c.Id, c.CompletedAt, c.Path!.Title }).ToListAsync();
        foreach (var c in careers) Add(XpSource.CareerCompleted, c.Id.ToString(), 500, $"Completed the career path “{c.Title}”", c.CompletedAt!.Value);

        var rounds = await db.PracticeRounds.Where(r => r.UserId == userId && r.SubmittedAt != null && r.XpEarned > 0)
            .Select(r => new { r.Id, r.XpEarned, r.Correct, r.Total, r.SubmittedAt }).ToListAsync();
        foreach (var r in rounds) Add(XpSource.Practice, r.Id.ToString(), r.XpEarned, $"AI practice round: {r.Correct}/{r.Total} correct", r.SubmittedAt!.Value);

        var vivas = await db.VivaSessions.Where(v => v.UserId == userId && v.Passed).Select(v => new { v.Id, v.ScorePercent, v.CompletedAt }).ToListAsync();
        foreach (var v in vivas) Add(XpSource.InterviewPassed, v.Id.ToString(), 100, $"Passed an AI interview ({v.ScorePercent}%)", v.CompletedAt ?? DateTime.UtcNow);

        foreach (var e in await db.Enrollments.Where(e => e.UserId == userId && e.Status != EnrollmentStatus.Revoked).ToListAsync())
            foreach (var m in (await milestones.ProgressAsync(e)).Where(m => m.Achieved))
                Add(XpSource.MilestoneAchieved, $"{e.Id}:{m.Id}", 40, $"Milestone reached: {m.Title}", DateTime.UtcNow);

        db.XpEvents.AddRange(fresh);
        await db.SaveChangesAsync();

        // Badges
        var metrics = await MetricsAsync(userId);
        var owned = (await db.UserBadges.Where(b => b.UserId == userId).Select(b => b.BadgeKey).ToListAsync()).ToHashSet();
        var earned = BadgeCatalog.All.Where(b => !owned.Contains(b.Key) && metrics.GetValueOrDefault(b.Metric) >= b.Target).ToList();
        db.UserBadges.AddRange(earned.Select(b => new UserBadge { UserId = userId, BadgeKey = b.Key }));
        await db.SaveChangesAsync();
        return earned;
    }

    public async Task<Dictionary<string, int>> MetricsAsync(Guid userId)
    {
        var events = await db.XpEvents.Where(x => x.UserId == userId).Select(x => new { x.Source, x.Points, x.OccurredAt }).ToListAsync();
        var rounds = await db.PracticeRounds.Where(r => r.UserId == userId && r.SubmittedAt != null).Select(r => new { r.Correct, r.Total }).ToListAsync();
        var (_, best) = Streak(events.Select(e => e.OccurredAt));
        return new Dictionary<string, int>
        {
            ["lessons"] = events.Count(e => e.Source == XpSource.LessonCompleted),
            ["classes"] = events.Count(e => e.Source == XpSource.ClassAttended),
            ["assessments"] = events.Count(e => e.Source == XpSource.AssessmentPassed),
            ["perfect"] = await db.AssessmentAttempts.CountAsync(a => a.UserId == userId && a.Passed && a.ScorePercent == 100),
            ["milestones"] = events.Count(e => e.Source == XpSource.MilestoneAchieved),
            ["certificates"] = events.Count(e => e.Source == XpSource.CourseCompleted),
            ["careers"] = events.Count(e => e.Source == XpSource.CareerCompleted),
            ["interviews"] = events.Count(e => e.Source == XpSource.InterviewPassed),
            ["practice"] = rounds.Sum(r => r.Correct),
            ["perfectRounds"] = rounds.Count(r => r.Total > 0 && r.Correct == r.Total),
            ["streak"] = best,
            ["xp"] = events.Sum(e => e.Points),
        };
    }

    /// <summary>Current streak (consecutive days ending today or yesterday) and best streak, in UTC days.</summary>
    public static (int Current, int Best) Streak(IEnumerable<DateTime> activity)
    {
        var days = activity.Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
        int best = 0, run = 0;
        for (var i = 0; i < days.Count; i++)
        {
            run = i > 0 && (days[i] - days[i - 1]).TotalDays == 1 ? run + 1 : 1;
            best = Math.Max(best, run);
        }
        var today = DateTime.UtcNow.Date;
        var current = days.Count > 0 && (today - days[^1]).TotalDays <= 1 ? run : 0;
        return (current, best);
    }
}

public static class GamificationEndpoints
{
    public static void MapGamification(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Gamification").RequireAuthorization();

        g.MapGet("/me/progress", async (CurrentUser me, TmsDbContext db, GamificationService game) =>
        {
            var userId = me.Id;
            await game.SyncAsync(userId);
            var metrics = await game.MetricsAsync(userId);
            var events = await db.XpEvents.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.OccurredAt).ToListAsync();
            var xp = events.Sum(e => e.Points);
            var level = GamificationService.LevelFor(xp);
            var (current, best) = GamificationService.Streak(events.Select(e => e.OccurredAt));
            var owned = await db.UserBadges.AsNoTracking().Where(b => b.UserId == userId).ToDictionaryAsync(b => b.BadgeKey, b => b.AwardedAt);
            var since = DateTime.UtcNow.Date.AddDays(-83);
            var weekAgo = DateTime.UtcNow.AddDays(-7);

            return new
            {
                Xp = xp,
                WeekXp = events.Where(e => e.OccurredAt >= weekAgo).Sum(e => e.Points),
                Level = level,
                Streak = new { Current = current, Best = best },
                Stats = metrics,
                Badges = BadgeCatalog.All.Select(b => new
                {
                    b.Key, b.Name, b.Description, b.Icon, b.Tier, b.Target,
                    Current = Math.Min(metrics.GetValueOrDefault(b.Metric), b.Target),
                    Earned = owned.ContainsKey(b.Key),
                    AwardedAt = owned.TryGetValue(b.Key, out var at) ? at : (DateTime?)null,
                }),
                Activity = events.Where(e => e.OccurredAt >= since).GroupBy(e => e.OccurredAt.Date)
                    .Select(d => new { Date = d.Key, Points = d.Sum(e => e.Points), Count = d.Count() }).OrderBy(d => d.Date),
                Recent = events.Take(12).Select(e => new { e.Source, e.Label, e.Points, e.OccurredAt }),
            };
        });

        g.MapGet("/leaderboard", async (string? period, CurrentUser me, TmsDbContext db) =>
        {
            var from = period == "all" ? DateTime.MinValue : DateTime.UtcNow.AddDays(-7);
            var totals = await db.XpEvents.AsNoTracking().Where(x => x.OccurredAt >= from)
                .GroupBy(x => x.UserId).Select(g => new { UserId = g.Key, Xp = g.Sum(x => x.Points) })
                .OrderByDescending(x => x.Xp).Take(50).ToListAsync();
            var ids = totals.Select(t => t.UserId).ToList();
            var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
            var userId = me.Id;
            // Only first name + last initial is shown to other learners.
            static string Display(string full)
            {
                var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.EndsWith('.')).ToArray();
                return parts.Length switch { 0 => "Learner", 1 => parts[0], _ => $"{parts[0]} {parts[^1][0]}." };
            }
            var rows = totals.Select((t, i) => new
            {
                Rank = i + 1,
                Name = t.UserId == userId ? "You" : Display(names.GetValueOrDefault(t.UserId) ?? ""),
                t.Xp,
                IsMe = t.UserId == userId,
            }).ToList();
            return new { Period = period == "all" ? "all" : "week", Rows = rows.Take(10), Me = rows.FirstOrDefault(r => r.IsMe) };
        });
    }
}
