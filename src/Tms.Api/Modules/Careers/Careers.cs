using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Careers;

public enum CareerStepKind { CompleteCourse, AttendClasses }

/// <summary>
/// Meet to Career: a role-based path (e.g. "Become a Forward Deployed Engineer") made of milestone steps —
/// courses to complete and live classes to attend. Finishing every step earns a career credential.
/// </summary>
public class CareerPath : Entity
{
    public required string Title { get; set; }
    public required string Slug { get; set; }
    /// <summary>The role the path prepares for, e.g. "Forward Deployed Engineer".</summary>
    public required string Role { get; set; }
    /// <summary>Short code used in credential ids, e.g. "FDE".</summary>
    public string CredentialPrefix { get; set; } = "CAREER";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Skills the role needs, one per line.</summary>
    public string Skills { get; set; } = "";
    public bool IsPublished { get; set; } = true;
    public List<CareerStep> Steps { get; set; } = [];
}

public class CareerStep : Entity
{
    public Guid PathId { get; set; }
    public int Order { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public CareerStepKind Kind { get; set; }
    public Guid? CourseId { get; set; }
    public Course? Course { get; set; }
    /// <summary>Live classes to attend, for AttendClasses steps.</summary>
    public int RequiredCount { get; set; }
}

public class CareerEnrollment : Entity
{
    public Guid PathId { get; set; }
    public CareerPath? Path { get; set; }
    public Guid UserId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CredentialCode { get; set; }
}

public record CareerStepProgress(Guid StepId, bool Achieved, int Current, int Total, Guid? EnrollmentId);
public record PathRequest(string? Title, string? Role, string? CredentialPrefix, string? Summary, string? Description, string? Skills, bool? IsPublished);
public record StepRequest(string? Title, string? Description, CareerStepKind? Kind, Guid? CourseId, int? RequiredCount, int? Order);

public class CareerService(TmsDbContext db)
{
    public async Task<List<CareerStepProgress>> ProgressAsync(CareerPath path, Guid userId)
    {
        var courseIds = path.Steps.Where(s => s.CourseId != null).Select(s => s.CourseId!.Value).ToList();
        var enrollments = await db.Enrollments.Where(e => e.UserId == userId && courseIds.Contains(e.CourseId) && e.Status != EnrollmentStatus.Revoked)
            .Select(e => new { e.Id, e.CourseId, e.Status }).ToListAsync();
        var certified = (await db.Certificates.Where(c => c.UserId == userId && courseIds.Contains(c.CourseId) && c.Status == CertificateStatus.Active)
            .Select(c => c.CourseId).ToListAsync()).ToHashSet();
        // Live classes count when they belong to a course on the path (any course, if the path has none).
        var attended = await db.SessionAttendance.CountAsync(a => a.UserId == userId && a.Status == AttendanceStatus.Present
            && db.ClassSessions.Any(s => s.Id == a.SessionId && (courseIds.Count == 0 || courseIds.Contains(s.CourseId))));

        return path.Steps.OrderBy(s => s.Order).Select(s =>
        {
            if (s.Kind == CareerStepKind.AttendClasses)
            {
                var required = Math.Max(1, s.RequiredCount);
                return new CareerStepProgress(s.Id, attended >= required, Math.Min(attended, required), required, null);
            }
            var e = enrollments.FirstOrDefault(x => x.CourseId == s.CourseId);
            var done = s.CourseId is { } cid && (certified.Contains(cid) || e?.Status == EnrollmentStatus.Completed);
            return new CareerStepProgress(s.Id, done, done ? 1 : 0, 1, e?.Id);
        }).ToList();
    }

    /// <summary>Awards the career credential on every path the user has now finished.</summary>
    public async Task RefreshAsync(Guid userId)
    {
        var open = await db.CareerEnrollments.Where(e => e.UserId == userId && e.CompletedAt == null).ToListAsync();
        foreach (var ce in open)
        {
            var path = await db.CareerPaths.Include(p => p.Steps).FirstAsync(p => p.Id == ce.PathId);
            if (path.Steps.Count == 0) continue;
            var progress = await ProgressAsync(path, userId);
            if (!progress.All(p => p.Achieved)) continue;
            ce.CompletedAt = DateTime.UtcNow;
            ce.CredentialCode = $"{path.CredentialPrefix}-{Text.RandomCode(4)}-{Text.RandomCode(4)}";
            Audit.Record(db, userId, "career.completed", nameof(CareerPath), path.Id, new { ce.CredentialCode });
        }
        await db.SaveChangesAsync();
    }
}

public static class CareerEndpoints
{
    public static void MapCareers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/careers").WithTags("Careers");

        g.MapGet("", async (CurrentUser me, TmsDbContext db, CareerService careers) =>
        {
            var paths = await db.CareerPaths.AsNoTracking().Include(p => p.Steps).ThenInclude(s => s.Course)
                .Where(p => p.IsPublished).OrderBy(p => p.CreatedAt).ToListAsync();
            var userId = me.IdOrNull;
            var mine = userId is null ? [] : await db.CareerEnrollments.AsNoTracking().Where(e => e.UserId == userId).ToListAsync();
            var result = new List<object>();
            foreach (var p in paths)
            {
                var ce = mine.FirstOrDefault(e => e.PathId == p.Id);
                var progress = ce is null ? null : await careers.ProgressAsync(p, userId!.Value);
                result.Add(new
                {
                    p.Id, p.Slug, p.Title, p.Role, p.Summary, p.CredentialPrefix,
                    Skills = p.Skills,
                    Steps = p.Steps.Count,
                    Courses = p.Steps.Count(s => s.CourseId != null),
                    Minutes = await CourseMinutesAsync(db, p.Steps.Where(s => s.CourseId != null).Select(s => s.CourseId!.Value)),
                    Learners = await db.CareerEnrollments.CountAsync(e => e.PathId == p.Id),
                    Joined = ce is not null,
                    StepsDone = progress?.Count(x => x.Achieved) ?? 0,
                    ce?.CompletedAt,
                    ce?.CredentialCode,
                });
            }
            return result;
        });

        g.MapGet("/{slug}", async (string slug, CurrentUser me, TmsDbContext db, CareerService careers) =>
        {
            var p = await db.CareerPaths.AsNoTracking().Include(x => x.Steps).ThenInclude(s => s.Course).ThenInclude(c => c!.Instructor)
                .FirstOrDefaultAsync(x => x.Slug == slug) ?? throw AppException.NotFound("Career path");
            var canManage = await me.HasPermissionAsync(Permissions.CourseEditAny);
            if (!p.IsPublished && !canManage) throw AppException.NotFound("Career path");
            var userId = me.IdOrNull;
            var ce = userId is null ? null : await db.CareerEnrollments.AsNoTracking().FirstOrDefaultAsync(e => e.PathId == p.Id && e.UserId == userId);
            var progress = userId is null ? [] : await careers.ProgressAsync(p, userId.Value);
            return new
            {
                p.Id, p.Slug, p.Title, p.Role, p.Summary, p.Description, p.Skills, p.CredentialPrefix, p.IsPublished,
                Learners = await db.CareerEnrollments.CountAsync(e => e.PathId == p.Id),
                Steps = p.Steps.OrderBy(s => s.Order).Select(s => new
                {
                    s.Id, s.Order, s.Title, s.Description, s.Kind, s.RequiredCount,
                    Course = s.Course is null ? null : new
                    {
                        s.Course.Id, s.Course.Slug, s.Course.Title, s.Course.Summary, s.Course.Level, s.Course.PriceMinor, s.Course.Currency,
                        Instructor = s.Course.Instructor?.FullName,
                    },
                    Progress = progress.FirstOrDefault(x => x.StepId == s.Id),
                }),
                Enrollment = ce is null ? null : new { ce.Id, ce.CreatedAt, ce.CompletedAt, ce.CredentialCode },
                CanManage = canManage,
            };
        });

        g.MapPost("/{pathId:guid}/join", async (Guid pathId, CurrentUser me, TmsDbContext db, CareerService careers) =>
        {
            var userId = me.Id;
            var path = await db.CareerPaths.FirstOrDefaultAsync(p => p.Id == pathId && p.IsPublished) ?? throw AppException.NotFound("Career path");
            if (!await db.CareerEnrollments.AnyAsync(e => e.PathId == pathId && e.UserId == userId))
            {
                db.CareerEnrollments.Add(new CareerEnrollment { PathId = path.Id, UserId = userId });
                await db.SaveChangesAsync();
                await careers.RefreshAsync(userId); // steps finished before joining still count
            }
            return Results.NoContent();
        }).RequireAuthorization();

        // Management: platform staff who can edit any course.
        var manage = api.MapGroup("").WithTags("Careers").RequireAuthorization();

        manage.MapPost("/careers", async (PathRequest req, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CourseEditAny);
            if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Role)) throw AppException.Invalid("A path needs a title and a role.");
            var slug = Text.Slugify(req.Title);
            if (await db.CareerPaths.AnyAsync(p => p.Slug == slug)) slug = $"{slug}-{Text.RandomCode(4).ToLowerInvariant()}";
            var path = new CareerPath { Title = req.Title.Trim(), Slug = slug, Role = req.Role.Trim() };
            Apply(path, req);
            db.CareerPaths.Add(path);
            await db.SaveChangesAsync();
            return new { path.Id, path.Slug };
        });

        manage.MapPatch("/careers/{pathId:guid}", async (Guid pathId, PathRequest req, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CourseEditAny);
            var path = await db.CareerPaths.FindAsync(pathId) ?? throw AppException.NotFound("Career path");
            if (req.Title is { Length: > 0 }) path.Title = req.Title.Trim();
            if (req.Role is { Length: > 0 }) path.Role = req.Role.Trim();
            Apply(path, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        manage.MapPost("/careers/{pathId:guid}/steps", async (Guid pathId, StepRequest req, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CourseEditAny);
            var path = await db.CareerPaths.FindAsync(pathId) ?? throw AppException.NotFound("Career path");
            var kind = req.Kind ?? CareerStepKind.CompleteCourse;
            Course? course = null;
            if (kind == CareerStepKind.CompleteCourse)
                course = await db.Courses.FirstOrDefaultAsync(c => c.Id == req.CourseId && c.Status == CourseStatus.Published)
                    ?? throw AppException.Invalid("Choose a published course.");
            var step = new CareerStep
            {
                PathId = path.Id,
                Kind = kind,
                CourseId = course?.Id,
                RequiredCount = kind == CareerStepKind.AttendClasses ? Math.Max(1, req.RequiredCount ?? 1) : 0,
                Title = req.Title is { Length: > 0 } t ? t.Trim() : course?.Title ?? $"Attend {Math.Max(1, req.RequiredCount ?? 1)} live classes",
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                Order = req.Order ?? (await db.CareerSteps.Where(s => s.PathId == path.Id).MaxAsync(s => (int?)s.Order) ?? 0) + 1,
            };
            db.CareerSteps.Add(step);
            await db.SaveChangesAsync();
            return new { step.Id };
        });

        manage.MapDelete("/career-steps/{stepId:guid}", async (Guid stepId, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CourseEditAny);
            var step = await db.CareerSteps.FindAsync(stepId) ?? throw AppException.NotFound("Step");
            db.CareerSteps.Remove(step);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static void Apply(CareerPath p, PathRequest req)
    {
        if (req.CredentialPrefix is { Length: > 0 }) p.CredentialPrefix = Text.Slugify(req.CredentialPrefix).Replace("-", "").ToUpperInvariant() is { Length: > 0 } c ? c[..Math.Min(8, c.Length)] : p.CredentialPrefix;
        if (req.Summary is not null) p.Summary = req.Summary.Trim();
        if (req.Description is not null) p.Description = req.Description.Trim();
        if (req.Skills is not null) p.Skills = req.Skills.Trim();
        if (req.IsPublished is { } pub) p.IsPublished = pub;
    }

    private static async Task<int> CourseMinutesAsync(TmsDbContext db, IEnumerable<Guid> courseIds)
    {
        var ids = courseIds.ToList();
        return await db.Lessons.Where(l => db.CourseModules.Any(m => m.Id == l.ModuleId
            && db.Courses.Any(c => ids.Contains(c.Id) && c.PublishedVersionId == m.CourseVersionId))).SumAsync(l => (int?)l.DurationMinutes) ?? 0;
    }
}
