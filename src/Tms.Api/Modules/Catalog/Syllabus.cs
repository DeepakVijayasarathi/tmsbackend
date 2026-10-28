using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Catalog;

public record MilestoneRequest(
    string? Title, string? Description, MilestoneKind? Kind, Guid? ModuleId, Guid? AssessmentId,
    int? RequiredCount, int? DueDays, bool? ClearDueDays, int? Order);

public record MilestoneProgress(
    Guid Id, string Title, string? Description, MilestoneKind Kind, string? Target,
    int Current, int Total, DateTime? DueAt, bool Achieved);

/// <summary>Works out which of a course version's milestones a learner has met.</summary>
public class MilestoneService(TmsDbContext db)
{
    public async Task<List<MilestoneProgress>> ProgressAsync(Enrollment e)
    {
        var milestones = await db.Milestones.AsNoTracking().Where(m => m.CourseVersionId == e.CourseVersionId).OrderBy(m => m.Order).ToListAsync();
        if (milestones.Count == 0) return [];

        var completed = (await db.LessonProgress.Where(p => p.EnrollmentId == e.Id).Select(p => p.LessonId).ToListAsync()).ToHashSet();
        var lessons = await db.Lessons.Where(l => l.Module!.CourseVersionId == e.CourseVersionId).Select(l => new { l.Id, l.ModuleId }).ToListAsync();
        var modules = await db.CourseModules.Where(m => m.CourseVersionId == e.CourseVersionId).ToDictionaryAsync(m => m.Id, m => m.Title);
        var assessments = await db.Assessments.Where(a => a.CourseVersionId == e.CourseVersionId).ToDictionaryAsync(a => a.Id, a => a.Title);
        var passed = (await db.AssessmentAttempts.Where(t => t.EnrollmentId == e.Id && t.Passed).Select(t => t.AssessmentId).Distinct().ToListAsync()).ToHashSet();
        var attended = await db.SessionAttendance.CountAsync(a => a.UserId == e.UserId && a.Status == AttendanceStatus.Present
            && db.ClassSessions.Any(s => s.Id == a.SessionId && s.CourseId == e.CourseId));

        return milestones.Select(m =>
        {
            DateTime? due = m.DueDays is { } d ? e.CreatedAt.AddDays(d) : null;
            switch (m.Kind)
            {
                case MilestoneKind.CompleteModule:
                {
                    var inModule = lessons.Where(l => l.ModuleId == m.ModuleId).Select(l => l.Id).ToList();
                    var done = inModule.Count(completed.Contains);
                    return new MilestoneProgress(m.Id, m.Title, m.Description, m.Kind, m.ModuleId is { } mid ? modules.GetValueOrDefault(mid) : null,
                        done, inModule.Count, due, inModule.Count > 0 && done == inModule.Count);
                }
                case MilestoneKind.PassAssessment:
                {
                    var ok = m.AssessmentId is { } aid && passed.Contains(aid);
                    return new MilestoneProgress(m.Id, m.Title, m.Description, m.Kind, m.AssessmentId is { } id ? assessments.GetValueOrDefault(id) : null,
                        ok ? 1 : 0, 1, due, ok);
                }
                default:
                {
                    var required = Math.Max(1, m.RequiredCount);
                    return new MilestoneProgress(m.Id, m.Title, m.Description, m.Kind, $"{required} live class{(required == 1 ? "" : "es")}",
                        Math.Min(attended, required), required, due, attended >= required);
                }
            }
        }).ToList();
    }
}

/// <summary>Instructor endpoints for the syllabus and milestones of a course's draft version.</summary>
public static class SyllabusEndpoints
{
    public static void MapSyllabus(this RouteGroupBuilder api)
    {
        var teach = api.MapGroup("").WithTags("Syllabus").RequireAuthorization();

        teach.MapPost("/courses/{courseId:guid}/milestones", async (Guid courseId, MilestoneRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            var draft = await authoring.DraftAsync(course);
            var milestone = new Milestone
            {
                CourseVersionId = draft.Id,
                Title = "",
                Order = req.Order ?? (await db.Milestones.Where(m => m.CourseVersionId == draft.Id).MaxAsync(m => (int?)m.Order) ?? 0) + 1,
            };
            await ApplyAsync(db, milestone, req, isNew: true);
            db.Milestones.Add(milestone);
            await db.SaveChangesAsync();
            return new { milestone.Id };
        });

        teach.MapPatch("/milestones/{milestoneId:guid}", async (Guid milestoneId, MilestoneRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var milestone = await db.Milestones.FindAsync(milestoneId) ?? throw AppException.NotFound("Milestone");
            await CatalogEndpoints.RequireDraftAsync(db, authoring, milestone.CourseVersionId);
            await ApplyAsync(db, milestone, req, isNew: false);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        teach.MapDelete("/milestones/{milestoneId:guid}", async (Guid milestoneId, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var milestone = await db.Milestones.FindAsync(milestoneId) ?? throw AppException.NotFound("Milestone");
            await CatalogEndpoints.RequireDraftAsync(db, authoring, milestone.CourseVersionId);
            db.Milestones.Remove(milestone);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task ApplyAsync(TmsDbContext db, Milestone m, MilestoneRequest req, bool isNew)
    {
        if (req.Title is { Length: > 0 }) m.Title = req.Title.Trim();
        if (string.IsNullOrWhiteSpace(m.Title)) throw AppException.Invalid("Give the milestone a title.");
        if (req.Description is not null) m.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        if (req.Kind is { } kind) m.Kind = kind;
        if (req.ModuleId is not null || isNew) m.ModuleId = req.ModuleId;
        if (req.AssessmentId is not null || isNew) m.AssessmentId = req.AssessmentId;
        if (req.RequiredCount is { } count) m.RequiredCount = Math.Max(1, count);
        if (req.DueDays is { } days) m.DueDays = Math.Max(0, days);
        if (req.ClearDueDays == true) m.DueDays = null;
        if (req.Order is { } order) m.Order = order;

        // The target must exist in the same (draft) version, otherwise progress could never be met.
        switch (m.Kind)
        {
            case MilestoneKind.CompleteModule:
                if (m.ModuleId is null || !await db.CourseModules.AnyAsync(x => x.Id == m.ModuleId && x.CourseVersionId == m.CourseVersionId))
                    throw AppException.Invalid("Choose a module from this course.");
                m.AssessmentId = null;
                break;
            case MilestoneKind.PassAssessment:
                if (m.AssessmentId is null || !await db.Assessments.AnyAsync(x => x.Id == m.AssessmentId && x.CourseVersionId == m.CourseVersionId))
                    throw AppException.Invalid("Choose an assessment from this course.");
                m.ModuleId = null;
                break;
            case MilestoneKind.AttendClasses:
                m.RequiredCount = Math.Max(1, m.RequiredCount);
                m.ModuleId = null;
                m.AssessmentId = null;
                break;
        }
    }
}
