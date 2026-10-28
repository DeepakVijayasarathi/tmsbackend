using Tms.Api.Modules.Assessments;

namespace Tms.Tests;

public class GraderTests
{
    private static Question Make(QuestionKind kind, params bool[] correct) => new()
    {
        Kind = kind,
        Prompt = "q",
        Points = 2,
        Options = correct.Select((c, i) => new QuestionOption { Text = $"o{i}", IsCorrect = c, Order = i }).ToList(),
    };

    [Fact]
    public void Single_choice_requires_the_one_correct_option()
    {
        var q = Make(QuestionKind.SingleChoice, false, true, false);
        Assert.True(Grader.Grade(q, [q.Options[1].Id]).IsCorrect);
        Assert.Equal(2, Grader.Grade(q, [q.Options[1].Id]).Points);
        Assert.False(Grader.Grade(q, [q.Options[0].Id]).IsCorrect);
        Assert.False(Grader.Grade(q, [q.Options[0].Id, q.Options[1].Id]).IsCorrect);
    }

    [Fact]
    public void Multiple_choice_is_all_or_nothing()
    {
        var q = Make(QuestionKind.MultipleChoice, true, true, false);
        Assert.True(Grader.Grade(q, [q.Options[0].Id, q.Options[1].Id]).IsCorrect);
        Assert.False(Grader.Grade(q, [q.Options[0].Id]).IsCorrect);
        Assert.False(Grader.Grade(q, [q.Options[0].Id, q.Options[1].Id, q.Options[2].Id]).IsCorrect);
    }

    [Fact]
    public void Option_ids_from_other_questions_are_ignored()
    {
        var q = Make(QuestionKind.SingleChoice, true, false);
        Assert.False(Grader.Grade(q, [Guid.NewGuid()]).IsCorrect);
    }
}
