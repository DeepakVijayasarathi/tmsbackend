using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Tms.Api.Modules.Gamification;

namespace Tms.Tests;

public class GamificationTests : IClassFixture<TmsFactory>
{
    private const string FreeCourse = "workplace-communication-essentials";
    private readonly TmsFactory _app;

    public GamificationTests(TmsFactory app) => _app = app;

    private async Task<(HttpClient Client, string EnrollmentId)> EnrolledLearnerAsync()
    {
        var email = $"gam-{Guid.NewGuid():N}@test.local";
        (await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Passw0rd!", fullName = "Game Learner", accountType = "learner" })).EnsureSuccessStatusCode();
        var client = await _app.LoginAsync(email);
        var courseId = (await client.GetJsonAsync($"/api/v1/courses/{FreeCourse}"))["id"]!.GetValue<string>();
        var enrollment = await client.PostJsonAsync($"/api/v1/courses/{courseId}/enrollments", new { });
        return (client, enrollment["id"]!.GetValue<string>());
    }

    [Fact]
    public void Streaks_count_consecutive_days_and_levels_follow_thresholds()
    {
        var today = DateTime.UtcNow.Date;
        Assert.Equal((3, 3), GamificationService.Streak([today, today.AddDays(-1), today.AddDays(-2).AddHours(5)]));
        Assert.Equal((1, 2), GamificationService.Streak([today, today.AddDays(-5), today.AddDays(-6)]));
        Assert.Equal((0, 2), GamificationService.Streak([today.AddDays(-3), today.AddDays(-4)]));
        Assert.Equal((0, 0), GamificationService.Streak([]));

        Assert.Equal(1, GamificationService.LevelFor(0).Number);
        Assert.Equal("Learner", GamificationService.LevelFor(100).Title);
        Assert.Equal(250, GamificationService.LevelFor(120).NextXp);
        Assert.Null(GamificationService.LevelFor(99_999).NextXp);
    }

    [Fact]
    public async Task Progress_derives_xp_and_badges_from_real_activity_without_double_counting()
    {
        var learner = await _app.LoginAsync("learner@tms.local"); // seeded: attended one live class
        var first = await learner.GetJsonAsync("/api/v1/me/progress");
        var second = await learner.GetJsonAsync("/api/v1/me/progress");

        Assert.True(first["xp"]!.GetValue<int>() >= 30);
        Assert.Equal(first["xp"]!.GetValue<int>(), second["xp"]!.GetValue<int>());
        var live = second["badges"]!.AsArray().Single(b => b!["key"]!.GetValue<string>() == "live-learner")!;
        Assert.True(live["earned"]!.GetValue<bool>());
        var bookworm = second["badges"]!.AsArray().Single(b => b!["key"]!.GetValue<string>() == "bookworm")!;
        Assert.False(bookworm["earned"]!.GetValue<bool>());
        Assert.Equal(10, bookworm["target"]!.GetValue<int>());
    }

    [Fact]
    public async Task Practice_round_from_the_question_bank_gives_instant_feedback_and_xp()
    {
        var (learner, enrollmentId) = await EnrolledLearnerAsync();
        _app.Llm.Reply = "not json"; // AI unusable → approved course questions are used

        var round = await learner.PostJsonAsync($"/api/v1/practice/{enrollmentId}/rounds", new { });
        Assert.Equal("bank", round["source"]!.GetValue<string>());
        Assert.Equal("Easy", round["difficulty"]!.GetValue<string>());
        var questions = round["questions"]!.AsArray();
        Assert.All(questions, q => Assert.Null(q!["correctIndex"])); // the key never leaves the server
        var id = round["id"]!.GetValue<string>();

        var earned = 0;
        for (var i = 0; i < questions.Count; i++)
        {
            var probe = await learner.PostJsonAsync($"/api/v1/practice/rounds/{id}/answers", new { index = i, option = 0 });
            earned += probe["points"]!.GetValue<int>();
            Assert.NotNull(probe["explanation"]);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await learner.PostAsJsonAsync($"/api/v1/practice/rounds/{id}/answers", new { index = 0, option = 1 })).StatusCode);

        var result = await learner.PostJsonAsync($"/api/v1/practice/rounds/{id}/finish", new { });
        var perfect = result["perfect"]!.GetValue<bool>();
        Assert.Equal(earned + (perfect ? 25 : 0), result["xpEarned"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Conflict, (await learner.PostAsync($"/api/v1/practice/rounds/{id}/finish", null)).StatusCode);

        var progress = await learner.GetJsonAsync("/api/v1/me/progress");
        Assert.Equal(result["xpEarned"]!.GetValue<int>(), progress["xp"]!.GetValue<int>());
    }

    [Fact]
    public async Task Ai_practice_uses_generated_questions_and_drops_malformed_ones()
    {
        var (learner, enrollmentId) = await EnrolledLearnerAsync();
        JsonObject Q(string prompt, int options, int correct) => new()
        {
            ["prompt"] = prompt, ["options"] = new JsonArray(Enumerable.Range(1, options).Select(i => (JsonNode)$"Option {i}").ToArray()),
            ["correctIndex"] = correct, ["explanation"] = "Because.", ["lesson"] = "Emails people act on",
        };
        _app.Llm.Reply = new JsonObject { ["questions"] = new JsonArray(Q("Good one", 4, 2), Q("Too few options", 3, 0), Q("Bad index", 4, 7), Q("Another", 4, 0)) }.ToJsonString();

        var round = await learner.PostJsonAsync($"/api/v1/practice/{enrollmentId}/rounds", new { });
        Assert.Equal("gemini", round["source"]!.GetValue<string>());
        Assert.Equal(2, round["total"]!.GetValue<int>());

        var id = round["id"]!.GetValue<string>();
        var right = await learner.PostJsonAsync($"/api/v1/practice/rounds/{id}/answers", new { index = 0, option = 2 });
        Assert.True(right["correct"]!.GetValue<bool>());
        Assert.Equal(10, right["points"]!.GetValue<int>()); // easy, no combo yet
        var wrong = await learner.PostJsonAsync($"/api/v1/practice/rounds/{id}/answers", new { index = 1, option = 3 });
        Assert.False(wrong["correct"]!.GetValue<bool>());
        Assert.Equal(0, wrong["combo"]!.GetValue<int>());

        // Someone else can't touch this round.
        var (other, _) = await EnrolledLearnerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/practice/rounds/{id}/finish", null)).StatusCode);
    }

    [Fact]
    public async Task Leaderboard_marks_the_current_learner()
    {
        var learner = await _app.LoginAsync("learner@tms.local");
        await learner.GetJsonAsync("/api/v1/me/progress");
        var board = await learner.GetJsonAsync("/api/v1/leaderboard?period=all");
        Assert.Contains(board["rows"]!.AsArray(), r => r!["isMe"]!.GetValue<bool>() && r["name"]!.GetValue<string>() == "You");
    }
}
