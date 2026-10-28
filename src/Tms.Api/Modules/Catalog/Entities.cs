using Tms.Api.Common;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Catalog;

public enum CourseStatus { Draft, Published, Archived }
public enum CourseVisibility { Public, TenantOnly }
public enum CourseLevel { Beginner, Intermediate, Advanced }
public enum LessonKind { Text, Video, Document, Link }
public enum MilestoneKind { CompleteModule, PassAssessment, AttendClasses }

/// <summary>
/// Course → CourseVersion → Modules → Lessons. Learners enrol against a published version, so
/// later edits never change the completion requirements of someone already enrolled.
/// </summary>
public class Course : Entity
{
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public Guid InstructorId { get; set; }
    public User? Instructor { get; set; }
    public required string Title { get; set; }
    public required string Slug { get; set; }
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "General";
    public CourseLevel Level { get; set; }
    public long PriceMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public CourseVisibility Visibility { get; set; }
    public CourseStatus Status { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<CourseVersion> Versions { get; set; } = [];
}

public class CourseVersion : Entity
{
    public Guid CourseId { get; set; }
    public Course? Course { get; set; }
    public int Number { get; set; }
    public bool IsDraft { get; set; } = true;
    public DateTime? PublishedAt { get; set; }

    /// <summary>Classroom completion requirement: sessions a learner must attend for a certificate.</summary>
    public int MinSessionsAttended { get; set; }

    /// <summary>Months a certificate stays valid; null means it never expires.</summary>
    public int? CertificateValidityMonths { get; set; }

    // Syllabus — versioned with the curriculum so enrolled learners keep the syllabus they signed up for.
    /// <summary>What a learner will be able to do, one outcome per line.</summary>
    public string LearningOutcomes { get; set; } = "";
    /// <summary>What a learner should know before starting, one item per line.</summary>
    public string Prerequisites { get; set; } = "";

    // AI interviewer (viva): an optional conversational graded test, required for the certificate when enabled.
    public bool VivaEnabled { get; set; }
    public int VivaPassMarkPercent { get; set; } = 70;
    public int VivaQuestionCount { get; set; } = 4;

    public List<CourseModule> Modules { get; set; } = [];
    public List<Assessments.Assessment> Assessments { get; set; } = [];
    public List<Milestone> Milestones { get; set; } = [];
}

public class CourseModule : Entity
{
    public Guid CourseVersionId { get; set; }
    public required string Title { get; set; }
    public int Order { get; set; }
    /// <summary>Syllabus week this module is taught in (1-based); null when the course is not paced by week.</summary>
    public int? Week { get; set; }
    /// <summary>Module learning objectives, one per line.</summary>
    public string? Objectives { get; set; }
    public List<Lesson> Lessons { get; set; } = [];
}

/// <summary>
/// A checkpoint on the learner's path, met automatically from their progress: finishing a module,
/// passing an assessment, or attending a number of live classes. Due a number of days after enrolment.
/// </summary>
public class Milestone : Entity
{
    public Guid CourseVersionId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public int Order { get; set; }
    public MilestoneKind Kind { get; set; }
    public Guid? ModuleId { get; set; }
    public Guid? AssessmentId { get; set; }
    /// <summary>Live classes to attend, for AttendClasses milestones.</summary>
    public int RequiredCount { get; set; }
    /// <summary>Days after enrolment the milestone is due; null means no deadline.</summary>
    public int? DueDays { get; set; }
}

public class Lesson : Entity
{
    public Guid ModuleId { get; set; }
    public CourseModule? Module { get; set; }
    public required string Title { get; set; }
    public int Order { get; set; }
    public LessonKind Kind { get; set; }
    public string Content { get; set; } = "";
    public string? MediaUrl { get; set; }
    public int DurationMinutes { get; set; }
    public bool IsPreview { get; set; }
}
