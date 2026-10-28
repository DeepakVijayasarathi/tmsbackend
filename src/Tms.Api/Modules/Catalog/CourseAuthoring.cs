using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Catalog;

/// <summary>
/// Authoring always edits the course's draft version. Publishing freezes the draft; the next edit
/// clones the published version into a fresh draft, so enrolled learners keep their original requirements.
/// </summary>
public class CourseAuthoring(TmsDbContext db, CurrentUser me)
{
    public async Task<Course> GetEditableAsync(Guid courseId)
    {
        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == courseId) ?? throw AppException.NotFound("Course");
        await RequireCanEditAsync(course);
        return course;
    }

    public async Task RequireCanEditAsync(Course course)
    {
        if (course.InstructorId == me.Id && await me.IsMemberOfAsync(course.TenantId)) return;
        await me.RequireAsync(Permissions.CourseEditAny, course.TenantId);
    }

    public async Task<bool> CanEditAsync(Course course)
    {
        if (me.IdOrNull is null) return false;
        if (course.InstructorId == me.Id) return true;
        return await me.HasPermissionAsync(Permissions.CourseEditAny, course.TenantId);
    }

    public async Task<CourseVersion> DraftAsync(Course course)
    {
        var draft = await db.CourseVersions.FirstOrDefaultAsync(v => v.CourseId == course.Id && v.IsDraft);
        if (draft is not null) return draft;

        var published = await db.CourseVersions
            .Include(v => v.Modules).ThenInclude(m => m.Lessons)
            .Include(v => v.Assessments).ThenInclude(a => a.Questions).ThenInclude(q => q.Options)
            .Include(v => v.Milestones)
            .AsSplitQuery()
            .Where(v => v.CourseId == course.Id)
            .OrderByDescending(v => v.Number)
            .FirstAsync();

        draft = new CourseVersion
        {
            CourseId = course.Id,
            Number = published.Number + 1,
            IsDraft = true,
            MinSessionsAttended = published.MinSessionsAttended,
            CertificateValidityMonths = published.CertificateValidityMonths,
            LearningOutcomes = published.LearningOutcomes,
            Prerequisites = published.Prerequisites,
            VivaEnabled = published.VivaEnabled,
            VivaPassMarkPercent = published.VivaPassMarkPercent,
            VivaQuestionCount = published.VivaQuestionCount,
            Modules = published.Modules.Select(m => new CourseModule
            {
                Title = m.Title,
                Order = m.Order,
                Week = m.Week,
                Objectives = m.Objectives,
                Lessons = m.Lessons.Select(l => new Lesson
                {
                    Title = l.Title, Order = l.Order, Kind = l.Kind, Content = l.Content,
                    MediaUrl = l.MediaUrl, DurationMinutes = l.DurationMinutes, IsPreview = l.IsPreview,
                }).ToList(),
            }).ToList(),
            Assessments = published.Assessments.Select(a => new Assessment
            {
                Title = a.Title, PassMarkPercent = a.PassMarkPercent, TimeLimitMinutes = a.TimeLimitMinutes,
                MaxAttempts = a.MaxAttempts, ShuffleQuestions = a.ShuffleQuestions,
                Questions = a.Questions.Select(q => new Question
                {
                    Kind = q.Kind, Prompt = q.Prompt, Explanation = q.Explanation, Points = q.Points, Order = q.Order,
                    Status = q.Status, AiGenerated = q.AiGenerated,
                    Options = q.Options.Select(o => new QuestionOption { Text = o.Text, IsCorrect = o.IsCorrect, Order = o.Order }).ToList(),
                }).ToList(),
            }).ToList(),
        };
        // Copies keep their original order, so the n-th module/assessment of the copy replaces the n-th original.
        var moduleMap = published.Modules.Zip(draft.Modules).ToDictionary(p => p.First.Id, p => p.Second.Id);
        var assessmentMap = published.Assessments.Zip(draft.Assessments).ToDictionary(p => p.First.Id, p => p.Second.Id);
        draft.Milestones = published.Milestones.Select(ms => new Milestone
        {
            Title = ms.Title, Description = ms.Description, Order = ms.Order, Kind = ms.Kind,
            ModuleId = ms.ModuleId is { } mid && moduleMap.TryGetValue(mid, out var newMid) ? newMid : null,
            AssessmentId = ms.AssessmentId is { } aid && assessmentMap.TryGetValue(aid, out var newAid) ? newAid : null,
            RequiredCount = ms.RequiredCount, DueDays = ms.DueDays,
        }).ToList();
        db.CourseVersions.Add(draft);
        await db.SaveChangesAsync();
        return draft;
    }

    public async Task PublishAsync(Course course)
    {
        await me.RequireAsync(Permissions.CoursePublish, course.TenantId);
        var draft = await db.CourseVersions.FirstOrDefaultAsync(v => v.CourseId == course.Id && v.IsDraft)
            ?? throw AppException.Conflict("There are no unpublished changes.");

        var lessonCount = await db.Lessons.CountAsync(l => db.CourseModules.Any(m => m.Id == l.ModuleId && m.CourseVersionId == draft.Id));
        if (lessonCount == 0) throw AppException.Invalid("Add at least one lesson before publishing.");

        var assessments = await db.Assessments.Where(a => a.CourseVersionId == draft.Id).Select(a => new
        {
            a.Title,
            Approved = db.Questions.Count(q => q.AssessmentId == a.Id && q.Status == QuestionStatus.Approved),
            Drafts = db.Questions.Count(q => q.AssessmentId == a.Id && q.Status == QuestionStatus.Draft),
        }).ToListAsync();
        if (assessments.FirstOrDefault(a => a.Drafts > 0) is { } pending)
            throw AppException.Invalid($"\"{pending.Title}\" has {pending.Drafts} unreviewed AI draft question(s). Approve or delete them first.");

        draft.IsDraft = false;
        draft.PublishedAt = DateTime.UtcNow;
        course.PublishedVersionId = draft.Id;
        course.Status = CourseStatus.Published;
        course.UpdatedAt = DateTime.UtcNow;
        Audit.Record(db, me.Id, "course.published", nameof(Course), course.Id, new { version = draft.Number }, course.TenantId);
        await db.SaveChangesAsync();
    }
}
