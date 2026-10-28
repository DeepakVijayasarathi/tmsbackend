using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;

namespace Tms.Api.Modules.Ai;

/// <summary>
/// Drafts a syllabus (outcomes, prerequisites, weekly plan) and milestones for a course's draft version from
/// its own lessons. Returns a proposal only — the instructor reviews it and applies it through the normal
/// syllabus and milestone endpoints. References to modules/assessments are validated against the draft.
/// </summary>
public static class SyllabusAssistant
{
    private const string System = """
        You are an instructional designer helping an instructor plan a course on TMS Learning.
        From the course outline and lesson excerpts, propose:
        - 3 to 6 learning outcomes (each starts with a verb, one line, specific to the material),
        - 1 to 4 prerequisites (or "None — open to everyone"),
        - for every module: a week number (1-based, in order) and 2 to 3 objectives grounded in its lessons,
        - 3 to 5 milestones a learner can be checked against automatically. kind is one of
          CompleteModule (set moduleId), PassAssessment (set assessmentId), AttendClasses (set requiredCount).
          Leave ids that don't apply as "". dueDays is days after enrolment, increasing.
        Use only the ids given. Do not invent content that is not in the material.
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> Schema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "learningOutcomes", "prerequisites", "modules", "milestones" }),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            learningOutcomes = new { type = "array", items = new { type = "string" } },
            prerequisites = new { type = "array", items = new { type = "string" } },
            modules = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    required = new[] { "moduleId", "week", "objectives" },
                    properties = new { moduleId = new { type = "string" }, week = new { type = "integer" }, objectives = new { type = "array", items = new { type = "string" } } },
                },
            },
            milestones = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    required = new[] { "title", "description", "kind", "moduleId", "assessmentId", "requiredCount", "dueDays" },
                    properties = new
                    {
                        title = new { type = "string" },
                        description = new { type = "string" },
                        kind = new { type = "string", @enum = new[] { "CompleteModule", "PassAssessment", "AttendClasses" } },
                        moduleId = new { type = "string" },
                        assessmentId = new { type = "string" },
                        requiredCount = new { type = "integer" },
                        dueDays = new { type = "integer" },
                    },
                },
            },
        }),
    };

    private sealed record DraftModule(string ModuleId, int Week, List<string> Objectives);
    private sealed record DraftMilestone(string Title, string Description, string Kind, string ModuleId, string AssessmentId, int RequiredCount, int DueDays);
    private sealed record Draft(List<string> LearningOutcomes, List<string> Prerequisites, List<DraftModule> Modules, List<DraftMilestone> Milestones);

    public static void MapSyllabusAssistant(this RouteGroupBuilder api)
    {
        api.MapPost("/courses/{courseId:guid}/syllabus-draft", async (Guid courseId, CourseAuthoring authoring, TmsDbContext db, ILlmClient llm, CancellationToken ct) =>
        {
            var course = await authoring.GetEditableAsync(courseId);
            var draft = await authoring.DraftAsync(course);
            var modules = await db.CourseModules.AsNoTracking().Where(m => m.CourseVersionId == draft.Id).OrderBy(m => m.Order)
                .Select(m => new { m.Id, m.Title, Lessons = m.Lessons.OrderBy(l => l.Order).Select(l => new { l.Title, l.Content, l.DurationMinutes }).ToList() })
                .ToListAsync(ct);
            if (modules.Count == 0) throw AppException.Invalid("Add modules and lessons first — the assistant plans from your material.");
            var assessments = await db.Assessments.AsNoTracking().Where(a => a.CourseVersionId == draft.Id).Select(a => new { a.Id, a.Title }).ToListAsync(ct);

            var prompt = JsonSerializer.Serialize(new
            {
                course = new { course.Title, course.Summary, course.Level, course.Category },
                modules = modules.Select(m => new
                {
                    id = m.Id, m.Title,
                    lessons = m.Lessons.Select(l => new { l.Title, minutes = l.DurationMinutes, excerpt = l.Content.Length > 600 ? l.Content[..600] : l.Content }),
                }),
                assessments = assessments.Select(a => new { id = a.Id, a.Title }),
            });
            var reply = await llm.CompleteAsync(System, [new ChatTurn("user", prompt)], Schema, ct);
            if (reply.Refused) throw new AppException(StatusCodes.Status422UnprocessableEntity, "The AI declined to plan this course.");

            Draft result;
            try { result = JsonSerializer.Deserialize<Draft>(reply.Text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!; }
            catch (JsonException) { throw new AppException(StatusCodes.Status502BadGateway, "The AI returned an unreadable plan. Try again."); }

            var moduleIds = modules.Select(m => m.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assessmentIds = assessments.Select(a => a.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Guid? Id(string value, HashSet<string> allowed) => allowed.Contains(value) ? Guid.Parse(value) : null;

            return new
            {
                LearningOutcomes = (result.LearningOutcomes ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Take(8),
                Prerequisites = (result.Prerequisites ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Take(6),
                Modules = (result.Modules ?? []).Where(m => moduleIds.Contains(m.ModuleId)).DistinctBy(m => m.ModuleId.ToLowerInvariant())
                    .Select(m => new { ModuleId = Guid.Parse(m.ModuleId), Week = Math.Max(1, m.Week), Objectives = m.Objectives.Where(o => o.Trim().Length > 0).Take(4) }),
                Milestones = (result.Milestones ?? []).Select(m => new
                {
                    m.Title, m.Description,
                    Kind = Enum.TryParse<MilestoneKind>(m.Kind, out var k) ? k : MilestoneKind.AttendClasses,
                    ModuleId = Id(m.ModuleId, moduleIds),
                    AssessmentId = Id(m.AssessmentId, assessmentIds),
                    RequiredCount = Math.Max(1, m.RequiredCount),
                    DueDays = Math.Max(0, m.DueDays),
                })
                // Keep only milestones whose target exists, so applying them can't fail validation.
                .Where(m => m.Kind == MilestoneKind.AttendClasses || (m.Kind == MilestoneKind.CompleteModule ? m.ModuleId != null : m.AssessmentId != null))
                .Take(6),
            };
        }).WithTags("Ai").RequireAuthorization();
    }
}
