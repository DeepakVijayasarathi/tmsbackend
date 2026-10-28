namespace Tms.Api.Modules.Assessments;

public record GradedAnswer(Guid QuestionId, bool IsCorrect, int Points);

/// <summary>All-or-nothing grading: the selected set must equal the correct set exactly.</summary>
public static class Grader
{
    public static GradedAnswer Grade(Question question, IReadOnlyCollection<Guid> selected)
    {
        var correct = question.Options.Where(o => o.IsCorrect).Select(o => o.Id).ToHashSet();
        var valid = question.Options.Select(o => o.Id).ToHashSet();
        var chosen = selected.Where(valid.Contains).ToHashSet();
        var isCorrect = correct.Count > 0 && chosen.SetEquals(correct)
            && (question.Kind == QuestionKind.MultipleChoice || chosen.Count == 1);
        return new GradedAnswer(question.Id, isCorrect, isCorrect ? question.Points : 0);
    }

    public static int Percent(int score, int max) => max == 0 ? 0 : (int)Math.Floor(score * 100m / max);
}
