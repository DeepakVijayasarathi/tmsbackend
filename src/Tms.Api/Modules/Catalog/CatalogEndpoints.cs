using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Assessments;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Catalog;

public record CreateCourseRequest(Guid TenantId, string Title, string? Summary, string? Category, CourseLevel? Level, long PriceMinor);
public record UpdateCourseRequest(
    string? Title, string? Summary, string? Description, string? Category, CourseLevel? Level,
    long? PriceMinor, CourseVisibility? Visibility, int? MinSessionsAttended, int? CertificateValidityMonths, bool? ClearCertificateValidity,
    string? LearningOutcomes = null, string? Prerequisites = null,
    bool? VivaEnabled = null, int? VivaPassMarkPercent = null, int? VivaQuestionCount = null);
public record ModuleRequest(string? Title, int? Order, int? Week = null, bool? ClearWeek = null, string? Objectives = null);
public record LessonRequest(string? Title, LessonKind? Kind, string? Content, string? MediaUrl, int? DurationMinutes, bool? IsPreview, int? Order);
public record AssessmentRequest(string? Title, int? PassMarkPercent, int? TimeLimitMinutes, int? MaxAttempts, bool? ShuffleQuestions);
public record OptionRequest(string Text, bool IsCorrect);
public record QuestionRequest(QuestionKind Kind, string Prompt, string? Explanation, int? Points, List<OptionRequest> Options);

public static class CatalogEndpoints
{
    public static void MapCatalog(this RouteGroupBuilder api)
    {
        var courses = api.MapGroup("/courses").WithTags("Catalog");

        courses.MapGet("", async (string? q, string? category, CourseLevel? level, CurrentUser me, TmsDbContext db) =>
        {
            var tenantIds = (await me.MembershipsAsync()).Select(m => m.TenantId).ToList();
            var query = db.Courses.AsNoTracking()
                .Where(c => c.Status == CourseStatus.Published && (c.Visibility == CourseVisibility.Public || tenantIds.Contains(c.TenantId)));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(c => c.Title.ToLower().Contains(term) || c.Summary.ToLower().Contains(term) || c.Category.ToLower().Contains(term));
            }
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(c => c.Category == category);
            if (level is not null) query = query.Where(c => c.Level == level);

            return await query.OrderByDescending(c => c.UpdatedAt).Select(c => new
            {
                c.Id, c.Slug, c.Title, c.Summary, c.Category, c.Level, c.PriceMinor, c.Currency,
                Instructor = c.Instructor!.FullName,
                Provider = c.Tenant!.Name,
                ProviderKind = c.Tenant.Kind,
                ProviderVerified = c.Tenant.IsVerified,
                Lessons = db.Lessons.Count(l => db.CourseModules.Any(m => m.Id == l.ModuleId && m.CourseVersionId == c.PublishedVersionId)),
                Minutes = db.Lessons.Where(l => db.CourseModules.Any(m => m.Id == l.ModuleId && m.CourseVersionId == c.PublishedVersionId)).Sum(l => l.DurationMinutes),
                Learners = db.Enrollments.Count(e => e.CourseId == c.Id && e.Status != EnrollmentStatus.Revoked),
            }).ToListAsync();
        });

        courses.MapGet("/categories", async (TmsDbContext db) =>
            await db.Courses.Where(c => c.Status == CourseStatus.Published).Select(c => c.Category).Distinct().OrderBy(c => c).ToListAsync());

        courses.MapGet("/{slug}", async (string slug, CurrentUser me, TmsDbContext db, CourseAuthoring authoring) =>
        {
            var course = await db.Courses.AsNoTracking().Include(c => c.Instructor).Include(c => c.Tenant)
                .FirstOrDefaultAsync(c => c.Slug == slug) ?? throw AppException.NotFound("Course");
            var canEdit = await authoring.CanEditAsync(course);
            var visible = course.Status == CourseStatus.Published
                && (course.Visibility == CourseVisibility.Public || await me.IsMemberOfAsync(course.TenantId));
            if (!visible && !canEdit) throw AppException.NotFound("Course");

            var versionId = course.PublishedVersionId;
            var modules = versionId is null ? [] : await db.CourseModules.AsNoTracking()
                .Where(m => m.CourseVersionId == versionId).OrderBy(m => m.Order)
                .Select(m => new
                {
                    m.Id, m.Title, m.Week, m.Objectives,
                    Lessons = m.Lessons.OrderBy(l => l.Order).Select(l => new { l.Id, l.Title, l.Kind, l.DurationMinutes, l.IsPreview }).ToList(),
                }).ToListAsync();
            var assessments = versionId is null ? [] : await db.Assessments.AsNoTracking().Where(a => a.CourseVersionId == versionId)
                .Select(a => new { a.Title, a.PassMarkPercent, Questions = a.Questions.Count(q => q.Status == QuestionStatus.Approved) })
                .ToListAsync();
            var version = versionId is null ? null : await db.CourseVersions.AsNoTracking().FirstAsync(v => v.Id == versionId);

            var enrollment = me.IdOrNull is { } uid
                ? await db.Enrollments.AsNoTracking().Where(e => e.CourseId == course.Id && e.UserId == uid && e.Status != EnrollmentStatus.Revoked)
                    .Select(e => new { e.Id, e.Status }).FirstOrDefaultAsync()
                : null;

            return new
            {
                course.Id, course.Slug, course.Title, course.Summary, course.Description, course.Category, course.Level,
                course.PriceMinor, course.Currency, course.Status, course.Visibility,
                Instructor = new { course.Instructor!.Id, course.Instructor.FullName, course.Instructor.Headline },
                Provider = new { course.Tenant!.Id, course.Tenant.Name, course.Tenant.Kind, course.Tenant.IsVerified },
                MinSessionsAttended = version?.MinSessionsAttended ?? 0,
                CertificateValidityMonths = version?.CertificateValidityMonths,
                LearningOutcomes = version?.LearningOutcomes ?? "",
                VivaEnabled = version?.VivaEnabled ?? false,
                Prerequisites = version?.Prerequisites ?? "",
                Modules = modules,
                Assessments = assessments,
                Milestones = versionId is null ? [] : await db.Milestones.AsNoTracking().Where(ms => ms.CourseVersionId == versionId).OrderBy(ms => ms.Order)
                    .Select(ms => new
                    {
                        ms.Id, ms.Title, ms.Description, ms.Kind, ms.RequiredCount, ms.DueDays,
                        Target = ms.Kind == MilestoneKind.CompleteModule ? db.CourseModules.Where(m => m.Id == ms.ModuleId).Select(m => m.Title).FirstOrDefault()
                            : ms.Kind == MilestoneKind.PassAssessment ? db.Assessments.Where(a => a.Id == ms.AssessmentId).Select(a => a.Title).FirstOrDefault()
                            : null,
                    }).ToListAsync(),
                Upcoming = await db.ClassSessions.AsNoTracking().Where(s => s.CourseId == course.Id && s.StartsAt > DateTime.UtcNow && s.Status != SessionStatus.Cancelled)
                    .OrderBy(s => s.StartsAt).Select(s => new { s.Id, s.Title, s.StartsAt, s.EndsAt, s.Location }).Take(5).ToListAsync(),
                Enrollment = enrollment,
                CanEdit = canEdit,
            };
        });

        courses.MapGet("/{courseId:guid}/lessons/{lessonId:guid}/preview", async (Guid courseId, Guid lessonId, TmsDbContext db) =>
        {
            var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == courseId && c.Status == CourseStatus.Published)
                ?? throw AppException.NotFound("Course");
            var lesson = await db.Lessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId && l.IsPreview
                && l.Module!.CourseVersionId == course.PublishedVersionId) ?? throw AppException.NotFound("Preview lesson");
            return new { lesson.Id, lesson.Title, lesson.Kind, lesson.Content, lesson.MediaUrl, lesson.DurationMinutes };
        });

        MapAuthoring(api);
    }

    private static void MapAuthoring(RouteGroupBuilder api)
    {
        var teach = api.MapGroup("").WithTags("Course authoring").RequireAuthorization();

        teach.MapGet("/teach/courses", async (CurrentUser me, TmsDbContext db) =>
        {
            var memberships = await me.MembershipsAsync();
            var editAnyTenants = memberships.Where(m => Permissions.For(m.Role).Contains(Permissions.CourseEditAny)).Select(m => m.TenantId).ToList();
            var platformEditor = memberships.Any(m => m.TenantKind == TenantKind.Platform && Permissions.For(m.Role).Contains(Permissions.CourseEditAny));
            var userId = me.Id;
            return await db.Courses.AsNoTracking()
                .Where(c => c.InstructorId == userId || editAnyTenants.Contains(c.TenantId) || platformEditor)
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    c.Id, c.Slug, c.Title, c.Status, c.PriceMinor, c.Currency, c.UpdatedAt,
                    Tenant = c.Tenant!.Name,
                    Instructor = c.Instructor!.FullName,
                    HasDraftChanges = db.CourseVersions.Any(v => v.CourseId == c.Id && v.IsDraft) && c.PublishedVersionId != null,
                    Learners = db.Enrollments.Count(e => e.CourseId == c.Id && e.Status != EnrollmentStatus.Revoked),
                })
                .ToListAsync();
        });

        teach.MapPost("/courses", async (CreateCourseRequest req, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CourseCreate, req.TenantId);
            if (string.IsNullOrWhiteSpace(req.Title)) throw AppException.Invalid("Give the course a title.");
            if (req.PriceMinor < 0) throw AppException.Invalid("Price cannot be negative.");

            var slug = Text.Slugify(req.Title);
            if (await db.Courses.AnyAsync(c => c.Slug == slug)) slug = $"{slug}-{Text.RandomCode(4).ToLower()}";
            var course = new Course
            {
                TenantId = req.TenantId,
                InstructorId = me.Id,
                Title = req.Title.Trim(),
                Slug = slug,
                Summary = req.Summary?.Trim() ?? "",
                Category = string.IsNullOrWhiteSpace(req.Category) ? "General" : req.Category.Trim(),
                Level = req.Level ?? CourseLevel.Beginner,
                PriceMinor = req.PriceMinor,
                Status = CourseStatus.Draft,
                Versions = [new CourseVersion { Number = 1, IsDraft = true }],
            };
            db.Courses.Add(course);
            Audit.Record(db, me.Id, "course.created", nameof(Course), course.Id, null, req.TenantId);
            await db.SaveChangesAsync();
            return Results.Created($"/api/v1/courses/{course.Id}", new { course.Id, course.Slug });
        });

        teach.MapGet("/courses/{courseId:guid}/draft", async (Guid courseId, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            var draft = await authoring.DraftAsync(course);
            var modules = await db.CourseModules.AsNoTracking().Where(m => m.CourseVersionId == draft.Id).OrderBy(m => m.Order)
                .Select(m => new
                {
                    m.Id, m.Title, m.Order, m.Week, m.Objectives,
                    Lessons = m.Lessons.OrderBy(l => l.Order)
                        .Select(l => new { l.Id, l.Title, l.Order, l.Kind, l.Content, l.MediaUrl, l.DurationMinutes, l.IsPreview }).ToList(),
                }).ToListAsync();
            var assessments = await db.Assessments.AsNoTracking().Where(a => a.CourseVersionId == draft.Id)
                .Select(a => new
                {
                    a.Id, a.Title, a.PassMarkPercent, a.TimeLimitMinutes, a.MaxAttempts, a.ShuffleQuestions,
                    Questions = a.Questions.OrderBy(q => q.Order).Select(q => new
                    {
                        q.Id, q.Kind, q.Prompt, q.Explanation, q.Points, q.Status, q.AiGenerated,
                        Options = q.Options.OrderBy(o => o.Order).Select(o => new { o.Id, o.Text, o.IsCorrect }).ToList(),
                    }).ToList(),
                }).ToListAsync();
            return new
            {
                course.Id, course.Slug, course.Title, course.Summary, course.Description, course.Category, course.Level,
                course.PriceMinor, course.Currency, course.Status, course.Visibility, course.TenantId,
                Draft = new { draft.Id, draft.Number, draft.MinSessionsAttended, draft.CertificateValidityMonths, draft.LearningOutcomes, draft.Prerequisites, draft.VivaEnabled, draft.VivaPassMarkPercent, draft.VivaQuestionCount },
                PublishedVersion = course.PublishedVersionId is null ? (int?)null
                    : await db.CourseVersions.Where(v => v.Id == course.PublishedVersionId).Select(v => v.Number).FirstAsync(),
                Modules = modules,
                Assessments = assessments,
                Milestones = await db.Milestones.AsNoTracking().Where(ms => ms.CourseVersionId == draft.Id).OrderBy(ms => ms.Order)
                    .Select(ms => new { ms.Id, ms.Title, ms.Description, ms.Order, ms.Kind, ms.ModuleId, ms.AssessmentId, ms.RequiredCount, ms.DueDays }).ToListAsync(),
            };
        });

        teach.MapPatch("/courses/{courseId:guid}", async (Guid courseId, UpdateCourseRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            if (req.Title is { Length: > 0 }) course.Title = req.Title.Trim();
            if (req.Summary is not null) course.Summary = req.Summary.Trim();
            if (req.Description is not null) course.Description = req.Description;
            if (req.Category is { Length: > 0 }) course.Category = req.Category.Trim();
            if (req.Level is not null) course.Level = req.Level.Value;
            if (req.Visibility is not null) course.Visibility = req.Visibility.Value;
            if (req.PriceMinor is not null)
            {
                if (req.PriceMinor < 0) throw AppException.Invalid("Price cannot be negative.");
                course.PriceMinor = req.PriceMinor.Value;
            }
            if (req.MinSessionsAttended is not null || req.CertificateValidityMonths is not null || req.ClearCertificateValidity == true
                || req.LearningOutcomes is not null || req.Prerequisites is not null
                || req.VivaEnabled is not null || req.VivaPassMarkPercent is not null || req.VivaQuestionCount is not null)
            {
                var draft = await authoring.DraftAsync(course);
                if (req.VivaEnabled is { } viva) draft.VivaEnabled = viva;
                if (req.VivaPassMarkPercent is { } vpm) draft.VivaPassMarkPercent = Math.Clamp(vpm, 1, 100);
                if (req.VivaQuestionCount is { } vqc) draft.VivaQuestionCount = Math.Clamp(vqc, 2, 8);
                if (req.LearningOutcomes is not null) draft.LearningOutcomes = req.LearningOutcomes.Trim();
                if (req.Prerequisites is not null) draft.Prerequisites = req.Prerequisites.Trim();
                if (req.MinSessionsAttended is { } min) draft.MinSessionsAttended = Math.Max(0, min);
                if (req.CertificateValidityMonths is { } months) draft.CertificateValidityMonths = months > 0 ? months : null;
                if (req.ClearCertificateValidity == true) draft.CertificateValidityMonths = null;
            }
            course.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        teach.MapPost("/courses/{courseId:guid}/publish", async (Guid courseId, CourseAuthoring authoring) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            await authoring.PublishAsync(course);
            return Results.NoContent();
        });

        // Modules
        teach.MapPost("/courses/{courseId:guid}/modules", async (Guid courseId, ModuleRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            var draft = await authoring.DraftAsync(course);
            var order = req.Order ?? (await db.CourseModules.Where(m => m.CourseVersionId == draft.Id).MaxAsync(m => (int?)m.Order) ?? 0) + 1;
            if (string.IsNullOrWhiteSpace(req.Title)) throw AppException.Invalid("Give the module a title.");
            var module = new CourseModule
            {
                CourseVersionId = draft.Id, Title = req.Title.Trim(), Order = order,
                Week = req.Week is > 0 ? req.Week : null,
                Objectives = string.IsNullOrWhiteSpace(req.Objectives) ? null : req.Objectives.Trim(),
            };
            db.CourseModules.Add(module);
            await db.SaveChangesAsync();
            return new { module.Id };
        });

        teach.MapPatch("/modules/{moduleId:guid}", async (Guid moduleId, ModuleRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var module = await db.CourseModules.FindAsync(moduleId) ?? throw AppException.NotFound("Module");
            await RequireDraftAsync(db, authoring, module.CourseVersionId);
            if (req.Title is { Length: > 0 }) module.Title = req.Title.Trim();
            if (req.Order is { } o) module.Order = o;
            if (req.Week is { } week) module.Week = week > 0 ? week : null;
            if (req.ClearWeek == true) module.Week = null;
            if (req.Objectives is not null) module.Objectives = string.IsNullOrWhiteSpace(req.Objectives) ? null : req.Objectives.Trim();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        teach.MapDelete("/modules/{moduleId:guid}", async (Guid moduleId, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var module = await db.CourseModules.Include(m => m.Lessons).FirstOrDefaultAsync(m => m.Id == moduleId) ?? throw AppException.NotFound("Module");
            await RequireDraftAsync(db, authoring, module.CourseVersionId);
            db.Lessons.RemoveRange(module.Lessons);
            db.Milestones.RemoveRange(db.Milestones.Where(ms => ms.ModuleId == module.Id));
            db.CourseModules.Remove(module);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Lessons
        teach.MapPost("/modules/{moduleId:guid}/lessons", async (Guid moduleId, LessonRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var module = await db.CourseModules.FindAsync(moduleId) ?? throw AppException.NotFound("Module");
            await RequireDraftAsync(db, authoring, module.CourseVersionId);
            if (string.IsNullOrWhiteSpace(req.Title)) throw AppException.Invalid("Give the lesson a title.");
            var lesson = new Lesson
            {
                ModuleId = moduleId,
                Title = req.Title.Trim(),
                Kind = req.Kind ?? LessonKind.Text,
                Content = req.Content ?? "",
                MediaUrl = req.MediaUrl,
                DurationMinutes = req.DurationMinutes ?? 10,
                IsPreview = req.IsPreview ?? false,
                Order = req.Order ?? (await db.Lessons.Where(l => l.ModuleId == moduleId).MaxAsync(l => (int?)l.Order) ?? 0) + 1,
            };
            db.Lessons.Add(lesson);
            await db.SaveChangesAsync();
            return new { lesson.Id };
        });

        teach.MapPatch("/lessons/{lessonId:guid}", async (Guid lessonId, LessonRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var lesson = await db.Lessons.Include(l => l.Module).FirstOrDefaultAsync(l => l.Id == lessonId) ?? throw AppException.NotFound("Lesson");
            await RequireDraftAsync(db, authoring, lesson.Module!.CourseVersionId);
            if (req.Title is { Length: > 0 }) lesson.Title = req.Title.Trim();
            if (req.Kind is not null) lesson.Kind = req.Kind.Value;
            if (req.Content is not null) lesson.Content = req.Content;
            if (req.MediaUrl is not null) lesson.MediaUrl = req.MediaUrl.Length == 0 ? null : req.MediaUrl;
            if (req.DurationMinutes is not null) lesson.DurationMinutes = req.DurationMinutes.Value;
            if (req.IsPreview is not null) lesson.IsPreview = req.IsPreview.Value;
            if (req.Order is not null) lesson.Order = req.Order.Value;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        teach.MapDelete("/lessons/{lessonId:guid}", async (Guid lessonId, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var lesson = await db.Lessons.Include(l => l.Module).FirstOrDefaultAsync(l => l.Id == lessonId) ?? throw AppException.NotFound("Lesson");
            await RequireDraftAsync(db, authoring, lesson.Module!.CourseVersionId);
            db.Lessons.Remove(lesson);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Assessments and questions
        teach.MapPost("/courses/{courseId:guid}/assessments", async (Guid courseId, AssessmentRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            var draft = await authoring.DraftAsync(course);
            var assessment = new Assessment { CourseVersionId = draft.Id, Title = string.IsNullOrWhiteSpace(req.Title) ? "Final assessment" : req.Title.Trim() };
            Apply(assessment, req);
            db.Assessments.Add(assessment);
            await db.SaveChangesAsync();
            return new { assessment.Id };
        });

        teach.MapPatch("/assessments/{assessmentId:guid}", async (Guid assessmentId, AssessmentRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var assessment = await db.Assessments.FindAsync(assessmentId) ?? throw AppException.NotFound("Assessment");
            await RequireDraftAsync(db, authoring, assessment.CourseVersionId);
            if (req.Title is { Length: > 0 }) assessment.Title = req.Title.Trim();
            Apply(assessment, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        teach.MapPost("/assessments/{assessmentId:guid}/questions", async (Guid assessmentId, QuestionRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var assessment = await db.Assessments.FindAsync(assessmentId) ?? throw AppException.NotFound("Assessment");
            await RequireDraftAsync(db, authoring, assessment.CourseVersionId);
            var question = BuildQuestion(req);
            question.AssessmentId = assessmentId;
            question.Order = (await db.Questions.Where(q => q.AssessmentId == assessmentId).MaxAsync(q => (int?)q.Order) ?? 0) + 1;
            db.Questions.Add(question);
            await db.SaveChangesAsync();
            return new { question.Id };
        });

        teach.MapPut("/questions/{questionId:guid}", async (Guid questionId, QuestionRequest req, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var question = await db.Questions.Include(q => q.Options).FirstOrDefaultAsync(q => q.Id == questionId) ?? throw AppException.NotFound("Question");
            var assessment = await db.Assessments.FirstAsync(a => a.Id == question.AssessmentId);
            await RequireDraftAsync(db, authoring, assessment.CourseVersionId);
            var updated = BuildQuestion(req);
            question.Kind = updated.Kind;
            question.Prompt = updated.Prompt;
            question.Explanation = updated.Explanation;
            question.Points = updated.Points;
            db.QuestionOptions.RemoveRange(question.Options);
            foreach (var o in updated.Options) { o.QuestionId = question.Id; db.QuestionOptions.Add(o); }
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        teach.MapPost("/questions/{questionId:guid}/approve", async (Guid questionId, CourseAuthoring authoring, CurrentUser me, TmsDbContext db) =>
        {
            var question = await db.Questions.FindAsync(questionId) ?? throw AppException.NotFound("Question");
            var assessment = await db.Assessments.FirstAsync(a => a.Id == question.AssessmentId);
            await RequireDraftAsync(db, authoring, assessment.CourseVersionId);
            question.Status = QuestionStatus.Approved;
            Audit.Record(db, me.Id, "question.approved", nameof(Question), question.Id, new { question.AiGenerated });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        teach.MapDelete("/questions/{questionId:guid}", async (Guid questionId, CourseAuthoring authoring, TmsDbContext db) =>
        {
            var question = await db.Questions.Include(q => q.Options).FirstOrDefaultAsync(q => q.Id == questionId) ?? throw AppException.NotFound("Question");
            var assessment = await db.Assessments.FirstAsync(a => a.Id == question.AssessmentId);
            await RequireDraftAsync(db, authoring, assessment.CourseVersionId);
            db.QuestionOptions.RemoveRange(question.Options);
            db.Questions.Remove(question);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    public static async Task<Course> RequireDraftAsync(TmsDbContext db, CourseAuthoring authoring, Guid versionId)
    {
        var version = await db.CourseVersions.Include(v => v.Course).FirstAsync(v => v.Id == versionId);
        await authoring.RequireCanEditAsync(version.Course!);
        if (!version.IsDraft) throw AppException.Conflict("Published versions are read-only. Reload the editor to get the current draft.");
        return version.Course!;
    }

    private static void Apply(Assessment a, AssessmentRequest req)
    {
        if (req.PassMarkPercent is { } pass) a.PassMarkPercent = Math.Clamp(pass, 1, 100);
        if (req.TimeLimitMinutes is { } limit) a.TimeLimitMinutes = limit > 0 ? limit : null;
        if (req.MaxAttempts is { } max) a.MaxAttempts = Math.Max(1, max);
        if (req.ShuffleQuestions is { } shuffle) a.ShuffleQuestions = shuffle;
    }

    public static Question BuildQuestion(QuestionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt)) throw AppException.Invalid("Question text is required.");
        var options = req.Kind == QuestionKind.TrueFalse && (req.Options is null || req.Options.Count == 0)
            ? throw AppException.Invalid("Mark True or False as correct.")
            : req.Options?.Where(o => !string.IsNullOrWhiteSpace(o.Text)).ToList() ?? [];
        if (options.Count < 2) throw AppException.Invalid("A question needs at least two options.");
        var correct = options.Count(o => o.IsCorrect);
        if (correct == 0) throw AppException.Invalid("Mark at least one option as correct.");
        if (req.Kind != QuestionKind.MultipleChoice && correct != 1) throw AppException.Invalid("Single-answer questions need exactly one correct option.");

        return new Question
        {
            Kind = req.Kind,
            Prompt = req.Prompt.Trim(),
            Explanation = req.Explanation?.Trim(),
            Points = Math.Max(1, req.Points ?? 1),
            Options = options.Select((o, i) => new QuestionOption { Text = o.Text.Trim(), IsCorrect = o.IsCorrect, Order = i }).ToList(),
        };
    }
}
