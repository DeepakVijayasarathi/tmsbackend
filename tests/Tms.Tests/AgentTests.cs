using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Data;
using Tms.Api.Modules.Learning;

namespace Tms.Tests;

/// <summary>The AI interviewer (viva) and the reminder &amp; follow-up agent.</summary>
public class AgentTests : IClassFixture<TmsFactory>
{
    private const string VivaCourse = "customer-deployments";
    private readonly TmsFactory _app;

    public AgentTests(TmsFactory app) => _app = app;

    private static string Turn(string message, bool followUp) => new JsonObject { ["message"] = message, ["followUp"] = followUp }.ToJsonString();
    private static string Result(int percent) => new JsonObject
    {
        ["overallPercent"] = percent,
        ["criteria"] = new JsonArray(new JsonObject { ["name"] = "Understanding", ["score"] = 14, ["comment"] = "Solid." }),
        ["strengths"] = new JsonArray("Clear plan"), ["improvements"] = new JsonArray("Mention rollback triggers"), ["summary"] = "Good work.",
    }.ToJsonString();

    /// <summary>A new learner enrolled (directly) in the paid course that ends with an AI interview.</summary>
    private async Task<(HttpClient Client, string EnrollmentId)> VivaLearnerAsync()
    {
        var email = $"viva-{Guid.NewGuid():N}@test.local";
        (await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Passw0rd!", fullName = "Viva Learner", accountType = "learner" })).EnsureSuccessStatusCode();
        await using var db = _app.Service<TmsDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        var course = await db.Courses.FirstAsync(c => c.Slug == VivaCourse);
        var enrollment = new Enrollment { UserId = user.Id, CourseId = course.Id, CourseVersionId = course.PublishedVersionId!.Value, Source = EnrollmentSource.Assigned, Status = EnrollmentStatus.Active };
        db.Enrollments.Add(enrollment);
        await db.SaveChangesAsync();
        return (await _app.LoginAsync(email), enrollment.Id.ToString());
    }

    [Fact]
    public async Task Ai_interviewer_asks_follows_up_once_then_scores_against_the_pass_mark()
    {
        var (learner, enrollmentId) = await VivaLearnerAsync();
        var info = await learner.GetJsonAsync($"/api/v1/viva/{enrollmentId}");
        Assert.True(info["enabled"]!.GetValue<bool>());
        Assert.Equal(3, info["vivaQuestionCount"]!.GetValue<int>());

        _app.Llm.Replies.Clear();
        _app.Llm.Replies.Enqueue(Turn("Hi Viva! What do you cover in a discovery call?", false));
        var s = await learner.PostJsonAsync($"/api/v1/viva/{enrollmentId}/sessions", new { });
        var id = s["id"]!.GetValue<string>();
        Assert.Equal(1, s["mainQuestions"]!.GetValue<int>());

        _app.Llm.Replies.Enqueue(Turn("Who signs off?", true));             // follow-up: still question 1
        s = await learner.PostJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "Goals and constraints." });
        Assert.Equal(1, s["mainQuestions"]!.GetValue<int>());
        Assert.True(s["transcript"]!.AsArray().Last()!["followUp"]!.GetValue<bool>());

        _app.Llm.Replies.Enqueue(Turn("How do you ship safely?", true));    // a second follow-up in a row is refused
        s = await learner.PostJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "The customer's sponsor." });
        Assert.Equal(2, s["mainQuestions"]!.GetValue<int>());
        Assert.False(s["transcript"]!.AsArray().Last()!["followUp"]!.GetValue<bool>());

        _app.Llm.Replies.Enqueue(Turn("What goes in a runbook?", false));
        s = await learner.PostJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "Feature flags and monitoring." });
        Assert.Equal(3, s["mainQuestions"]!.GetValue<int>());

        _app.Llm.Replies.Enqueue(Turn("Done.", false));                      // no follow-up on the final answer
        _app.Llm.Replies.Enqueue(Result(80));
        s = await learner.PostJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "Start/stop steps, logs, common errors." });
        Assert.Equal("Completed", s["status"]!.GetValue<string>());
        Assert.True(s["passed"]!.GetValue<bool>());
        Assert.Equal(80, s["scorePercent"]!.GetValue<int>());
        Assert.Equal(10, s["result"]!["criteria"]!.AsArray()[0]!["score"]!.GetValue<int>()); // clamped to 0–10

        Assert.Equal(HttpStatusCode.Conflict, (await learner.PostAsJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "more" })).StatusCode);
        var player = await learner.GetJsonAsync($"/api/v1/enrollments/{enrollmentId}");
        Assert.True(player["eligibility"]!["vivaRequired"]!.GetValue<bool>());
        Assert.True(player["eligibility"]!["vivaPassed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Interview_answers_survive_a_scoring_failure_and_the_retry_scores_once()
    {
        var (learner, enrollmentId) = await VivaLearnerAsync();
        _app.Llm.Replies.Clear();
        _app.Llm.Replies.Enqueue(Turn("Q1", false));
        var id = (await learner.PostJsonAsync($"/api/v1/viva/{enrollmentId}/sessions", new { }))["id"]!.GetValue<string>();
        _app.Llm.Replies.Enqueue(Turn("Q2", false));
        await learner.PostJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "A1" });
        _app.Llm.Replies.Enqueue(Turn("Q3", false));
        await learner.PostJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "A2" });

        _app.Llm.Replies.Enqueue(Turn("ok", false));
        _app.Llm.Replies.Enqueue("not json");                                 // scoring fails
        var failed = await learner.PostAsJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "A3" });
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);

        _app.Llm.Replies.Enqueue(Result(40));
        var s = await learner.PostJsonAsync($"/api/v1/viva/sessions/{id}/answers", new { answer = "ignored on retry" });
        Assert.Equal("Completed", s["status"]!.GetValue<string>());
        Assert.False(s["passed"]!.GetValue<bool>());
        var learnerTurns = s["transcript"]!.AsArray().Where(t => t!["role"]!.GetValue<string>() == "learner").Select(t => t!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["A1", "A2", "A3"], learnerTurns);
    }

    [Fact]
    public async Task Interview_is_only_available_on_courses_that_enable_it()
    {
        var learner = await _app.LoginAsync("learner@tms.local"); // enrolled in the communication course (no viva)
        var enrollmentId = (await learner.GetJsonAsync("/api/v1/me/enrollments")).AsArray().First()!["id"]!.GetValue<string>();
        Assert.False((await learner.GetJsonAsync($"/api/v1/viva/{enrollmentId}"))["enabled"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await learner.PostAsJsonAsync($"/api/v1/viva/{enrollmentId}/sessions", new { })).StatusCode);
    }

    [Fact]
    public async Task Reminder_agent_raises_each_reminder_once_and_tracks_read_state()
    {
        var learner = await _app.LoginAsync("learner@tms.local");
        await learner.GetJsonAsync("/api/v1/me/progress"); // awards the seeded Live Learner badge

        var first = await learner.GetJsonAsync("/api/v1/me/notifications");
        var kinds = first["items"]!.AsArray().Select(i => i!["kind"]!.GetValue<string>()).ToList();
        Assert.Contains("ClassStartingSoon", kinds); // seeded workshop starts within the hour
        Assert.Contains("BadgeEarned", kinds);
        Assert.Equal(first["items"]!.AsArray().Count, first["unread"]!.GetValue<int>());

        var again = await learner.GetJsonAsync("/api/v1/me/notifications");
        Assert.Equal(first["items"]!.AsArray().Count, again["items"]!.AsArray().Count);

        var one = again["items"]!.AsArray()[0]!["id"]!.GetValue<string>();
        (await learner.PostAsync($"/api/v1/me/notifications/{one}/read", null)).EnsureSuccessStatusCode();
        Assert.Equal(first["unread"]!.GetValue<int>() - 1, (await learner.GetJsonAsync("/api/v1/me/notifications"))["unread"]!.GetValue<int>());
        (await learner.PostAsync("/api/v1/me/notifications/read-all", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await learner.GetJsonAsync("/api/v1/me/notifications"))["unread"]!.GetValue<int>());

        // Notifications are private.
        var other = await _app.LoginAsync("prof@tms.local");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/me/notifications/{one}/read", null)).StatusCode);
    }
}
