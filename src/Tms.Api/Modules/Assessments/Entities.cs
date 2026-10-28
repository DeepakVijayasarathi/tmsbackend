using Tms.Api.Common;

namespace Tms.Api.Modules.Assessments;

public enum QuestionKind { SingleChoice, MultipleChoice, TrueFalse }
public enum QuestionStatus { Draft, Approved }
public enum AttemptStatus { InProgress, Submitted }

public class Assessment : Entity
{
    public Guid CourseVersionId { get; set; }
    public required string Title { get; set; }
    public int PassMarkPercent { get; set; } = 70;
    public int? TimeLimitMinutes { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public bool ShuffleQuestions { get; set; } = true;
    public List<Question> Questions { get; set; } = [];
}

public class Question : Entity
{
    public Guid AssessmentId { get; set; }
    public QuestionKind Kind { get; set; }
    public required string Prompt { get; set; }
    public string? Explanation { get; set; }
    public int Points { get; set; } = 1;
    public int Order { get; set; }

    /// <summary>AI-generated questions start as drafts and are excluded from attempts until a trainer approves them.</summary>
    public QuestionStatus Status { get; set; } = QuestionStatus.Approved;
    public bool AiGenerated { get; set; }
    public List<QuestionOption> Options { get; set; } = [];
}

public class QuestionOption : Entity
{
    public Guid QuestionId { get; set; }
    public required string Text { get; set; }
    public bool IsCorrect { get; set; }
    public int Order { get; set; }
}

public class AssessmentAttempt : Entity
{
    public Guid AssessmentId { get; set; }
    public Assessment? Assessment { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid UserId { get; set; }
    public AttemptStatus Status { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Comma-separated question ids in the order this learner was shown them.</summary>
    public string QuestionOrder { get; set; } = "";
    public int ScorePoints { get; set; }
    public int MaxPoints { get; set; }
    public int ScorePercent { get; set; }
    public bool Passed { get; set; }
    public List<AttemptAnswer> Answers { get; set; } = [];
}

public class AttemptAnswer : Entity
{
    public Guid AttemptId { get; set; }
    public Guid QuestionId { get; set; }

    /// <summary>Comma-separated selected option ids.</summary>
    public string SelectedOptionIds { get; set; } = "";
    public bool IsCorrect { get; set; }
    public int PointsAwarded { get; set; }
}
