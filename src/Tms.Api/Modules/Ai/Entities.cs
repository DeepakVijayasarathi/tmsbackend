using Tms.Api.Common;

namespace Tms.Api.Modules.Ai;

public class AiMessage : Entity
{
    public Guid UserId { get; set; }
    public Guid CourseId { get; set; }
    public required string Role { get; set; }
    public required string Content { get; set; }

    /// <summary>Comma-separated lesson ids the answer was grounded in.</summary>
    public string? SourceLessonIds { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
