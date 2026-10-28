using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tms.Tests;

public class ClassroomTests : IClassFixture<TmsFactory>
{
    private const string FreeCourse = "workplace-communication-essentials";
    private readonly TmsFactory _app;

    public ClassroomTests(TmsFactory app) => _app = app;

    private async Task<string> FreeCourseIdAsync() =>
        (await _app.CreateClient().GetJsonAsync($"/api/v1/courses/{FreeCourse}"))["id"]!.GetValue<string>();

    private async Task<string> ScheduleAsync(HttpClient host, DateTime startsAt, int minutes = 60)
    {
        var res = await host.PostJsonAsync($"/api/v1/courses/{await FreeCourseIdAsync()}/sessions", new
        {
            title = "Test class " + Guid.NewGuid().ToString("N")[..6],
            startsAt, endsAt = startsAt.AddMinutes(minutes), capacity = 10,
        });
        return res["id"]!.GetValue<string>();
    }

    private async Task<HttpClient> NewLearnerAsync(bool enrol)
    {
        var email = $"learner-{Guid.NewGuid():N}@test.local";
        var reg = await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Passw0rd!", fullName = "Test Learner", accountType = "learner" });
        reg.EnsureSuccessStatusCode();
        var client = await _app.LoginAsync(email);
        if (enrol) await client.PostJsonAsync($"/api/v1/courses/{await FreeCourseIdAsync()}/enrollments", new { });
        return client;
    }

    private static JwtSecurityToken ReadToken(JsonElement join) => new JwtSecurityTokenHandler().ReadJwtToken(join.GetProperty("token").GetString());

    [Fact]
    public async Task Learner_joining_a_class_gets_a_room_token_and_is_marked_present()
    {
        var prof = await _app.LoginAsync("prof@tms.local");
        var id = await ScheduleAsync(prof, DateTime.UtcNow.AddMinutes(5));
        var learner = await _app.LoginAsync("learner@tms.local");

        var join = await (await learner.PostAsync($"/api/v1/sessions/{id}/join", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("meet.test.local", join.GetProperty("domain").GetString());
        var room = join.GetProperty("room").GetString()!;
        Assert.StartsWith("tms-test-class-", room);
        Assert.False(join.GetProperty("isHost").GetBoolean());
        var token = ReadToken(join);
        Assert.Equal(room, token.Payload["room"]!.ToString());
        Assert.Equal("tms-test", token.Issuer);
        Assert.Contains("\"moderator\":false", JsonSerializer.Serialize(token.Payload["context"]));

        var attendance = await prof.GetJsonAsync($"/api/v1/sessions/{id}/attendance");
        var me = attendance.AsArray().Single(a => a!["email"]!.GetValue<string>() == "learner@tms.local")!;
        Assert.Equal("Present", me["status"]!.GetValue<string>());
        Assert.NotNull(me["joinedAt"]);
    }

    [Fact]
    public async Task Host_starts_the_class_and_ending_it_marks_no_shows_absent()
    {
        var prof = await _app.LoginAsync("prof@tms.local");
        var id = await ScheduleAsync(prof, DateTime.UtcNow.AddMinutes(30));
        var noShow = await NewLearnerAsync(enrol: true);
        (await noShow.PostAsync($"/api/v1/sessions/{id}/register", null)).EnsureSuccessStatusCode();

        var join = await (await prof.PostAsync($"/api/v1/sessions/{id}/join", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(join.GetProperty("isHost").GetBoolean());
        Assert.Contains("\"moderator\":true", JsonSerializer.Serialize(ReadToken(join).Payload["context"]));
        Assert.Equal("Live", (await prof.GetJsonAsync($"/api/v1/sessions/{id}"))["status"]!.GetValue<string>());

        (await prof.PostAsync($"/api/v1/sessions/{id}/end", null)).EnsureSuccessStatusCode();

        var detail = await prof.GetJsonAsync($"/api/v1/sessions/{id}");
        Assert.Equal("Ended", detail["status"]!.GetValue<string>());
        var attendance = await prof.GetJsonAsync($"/api/v1/sessions/{id}/attendance");
        Assert.All(attendance.AsArray(), a => Assert.Equal("Absent", a!["status"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Conflict, (await noShow.PostAsync($"/api/v1/sessions/{id}/join", null)).StatusCode);
    }

    [Fact]
    public async Task Only_enrolled_learners_can_join_and_only_hosts_can_end()
    {
        var prof = await _app.LoginAsync("prof@tms.local");
        var id = await ScheduleAsync(prof, DateTime.UtcNow.AddMinutes(5));
        var outsider = await NewLearnerAsync(enrol: false);
        var enrolled = await NewLearnerAsync(enrol: true);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync($"/api/v1/sessions/{id}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/v1/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await enrolled.PostAsync($"/api/v1/sessions/{id}/end", null)).StatusCode);
    }

    [Fact]
    public async Task Classroom_opens_shortly_before_the_start_and_lists_in_my_classes()
    {
        var prof = await _app.LoginAsync("prof@tms.local");
        var id = await ScheduleAsync(prof, DateTime.UtcNow.AddDays(2));
        var learner = await NewLearnerAsync(enrol: true);

        Assert.Equal(HttpStatusCode.Conflict, (await learner.PostAsync($"/api/v1/sessions/{id}/join", null)).StatusCode);
        var detail = await learner.GetJsonAsync($"/api/v1/sessions/{id}");
        Assert.False(detail["canJoin"]!.GetValue<bool>());

        var upcoming = await learner.GetJsonAsync("/api/v1/me/sessions");
        var mine = upcoming.AsArray().Single(s => s!["id"]!.GetValue<string>() == id)!;
        Assert.False(mine["isHost"]!.GetValue<bool>());
        var hosting = await prof.GetJsonAsync("/api/v1/me/sessions");
        Assert.True(hosting.AsArray().Single(s => s!["id"]!.GetValue<string>() == id)!["isHost"]!.GetValue<bool>());

        (await prof.PostAsync($"/api/v1/sessions/{id}/cancel", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await learner.GetJsonAsync("/api/v1/me/sessions")).AsArray(), s => s!["id"]!.GetValue<string>() == id);
    }
}
