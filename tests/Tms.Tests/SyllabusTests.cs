using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Tms.Tests;

public class SyllabusTests : IClassFixture<TmsFactory>
{
    private const string FreeCourse = "workplace-communication-essentials";
    private readonly TmsFactory _app;

    public SyllabusTests(TmsFactory app) => _app = app;

    private async Task<HttpClient> NewLearnerAsync()
    {
        var email = $"syl-{Guid.NewGuid():N}@test.local";
        (await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Passw0rd!", fullName = "Syllabus Learner", accountType = "learner" })).EnsureSuccessStatusCode();
        return await _app.LoginAsync(email);
    }

    [Fact]
    public async Task Course_page_shows_syllabus_weeks_and_milestones()
    {
        var course = await _app.CreateClient().GetJsonAsync($"/api/v1/courses/{FreeCourse}");

        Assert.Contains("first line", course["learningOutcomes"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(course["prerequisites"]!.GetValue<string>()));
        var module = course["modules"]!.AsArray()[0]!;
        Assert.Equal(1, module["week"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(module["objectives"]!.GetValue<string>()));

        var milestones = course["milestones"]!.AsArray();
        Assert.Equal(3, milestones.Count);
        Assert.Equal("Clear communication", milestones[0]!["target"]!.GetValue<string>());
        Assert.Equal("Quick check", milestones[2]!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task Milestones_are_met_automatically_from_learner_progress()
    {
        var learner = await NewLearnerAsync();
        var courseId = (await learner.GetJsonAsync($"/api/v1/courses/{FreeCourse}"))["id"]!.GetValue<string>();
        var enrollmentId = (await learner.PostJsonAsync($"/api/v1/courses/{courseId}/enrollments", new { }))["id"]!.GetValue<string>();

        var player = await learner.GetJsonAsync($"/api/v1/enrollments/{enrollmentId}");
        var first = player["milestones"]!.AsArray()[0]!;
        Assert.False(first["achieved"]!.GetValue<bool>());
        Assert.Equal(2, first["total"]!.GetValue<int>());
        var enrolled = DateTime.Parse(player["createdAt"]!.GetValue<string>()).ToUniversalTime();
        var due = DateTime.Parse(first["dueAt"]!.GetValue<string>()).ToUniversalTime();
        Assert.Equal(3, Math.Round((due - enrolled).TotalDays));

        foreach (var lesson in player["modules"]!.AsArray()[0]!["lessons"]!.AsArray())
            (await learner.PutAsync($"/api/v1/enrollments/{enrollmentId}/lessons/{lesson!["id"]}/progress", null)).EnsureSuccessStatusCode();

        var milestones = (await learner.GetJsonAsync($"/api/v1/enrollments/{enrollmentId}"))["milestones"]!.AsArray();
        Assert.True(milestones[0]!["achieved"]!.GetValue<bool>());
        Assert.False(milestones[1]!["achieved"]!.GetValue<bool>()); // live class not attended yet
        Assert.False(milestones[2]!["achieved"]!.GetValue<bool>()); // assessment not passed yet
    }

    [Fact]
    public async Task Instructors_edit_syllabus_and_milestones_on_the_draft()
    {
        var prof = await _app.LoginAsync("prof@tms.local");
        var courseId = (await prof.GetJsonAsync("/api/v1/courses/practical-data-analysis-with-python"))["id"]!.GetValue<string>();

        (await prof.PatchAsJsonAsync($"/api/v1/courses/{courseId}", new { learningOutcomes = "Outcome A\nOutcome B", prerequisites = "Python" })).EnsureSuccessStatusCode();
        var draft = await prof.GetJsonAsync($"/api/v1/courses/{courseId}/draft");
        Assert.Equal("Outcome A\nOutcome B", draft["draft"]!["learningOutcomes"]!.GetValue<string>());

        // Milestones copied into the draft point at the draft's own modules, not the published ones.
        var draftModuleIds = draft["modules"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToHashSet();
        var copied = draft["milestones"]!.AsArray();
        Assert.Equal(4, copied.Count);
        Assert.All(copied.Where(m => m!["kind"]!.GetValue<string>() == "CompleteModule"), m => Assert.Contains(m!["moduleId"]!.GetValue<string>(), draftModuleIds));

        var bad = await prof.PostAsJsonAsync($"/api/v1/courses/{courseId}/milestones", new { title = "Bad", kind = "CompleteModule", moduleId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await prof.PostJsonAsync($"/api/v1/courses/{courseId}/milestones", new { title = "Attend two labs", kind = "AttendClasses", requiredCount = 2, dueDays = 30 });
        var id = created["id"]!.GetValue<string>();
        (await prof.PatchAsJsonAsync($"/api/v1/milestones/{id}", new { clearDueDays = true })).EnsureSuccessStatusCode();
        var after = (await prof.GetJsonAsync($"/api/v1/courses/{courseId}/draft"))["milestones"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == id)!;
        Assert.Equal(2, after["requiredCount"]!.GetValue<int>());
        Assert.Null(after["dueDays"]);

        var learner = await NewLearnerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.DeleteAsync($"/api/v1/milestones/{id}")).StatusCode);
    }
}
