using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tms.Api.Data;
using Tms.Api.Modules.Careers;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Commerce.Providers;
using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Learning;

namespace Tms.Tests;

public class CareerTests : IClassFixture<TmsFactory>
{
    private const string PaidCourse = "practical-data-analysis-with-python";
    private readonly TmsFactory _app;

    public CareerTests(TmsFactory app) => _app = app;

    private async Task<(HttpClient Client, string Email)> NewLearnerAsync()
    {
        var email = $"car-{Guid.NewGuid():N}@test.local";
        (await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Passw0rd!", fullName = "Career Learner", accountType = "learner" })).EnsureSuccessStatusCode();
        return (await _app.LoginAsync(email), email);
    }

    [Fact]
    public void Referral_share_splits_into_stages_that_sum_exactly()
    {
        var parts = CommissionCalculator.SplitReferral(1001, "Purchase:30,FirstMilestone:30,CourseCompleted:40");
        Assert.Equal([300L, 300L, 401L], parts.Select(p => p.AmountMinor));
        Assert.Equal(ReferralStage.CourseCompleted, parts[2].Stage);

        // Schedules that don't add up to 100% (or don't parse) pay everything at purchase.
        Assert.Single(CommissionCalculator.SplitReferral(1001, "Purchase:50,CourseCompleted:40"));
        Assert.Single(CommissionCalculator.SplitReferral(1001, "Soon:100"));
        Assert.Equal(1001, CommissionCalculator.SplitReferral(1001, "").Single().AmountMinor);
    }

    [Fact]
    public async Task Held_referral_commission_is_released_when_the_referred_learner_hits_a_milestone()
    {
        var (buyer, _) = await NewLearnerAsync();
        var course = await _app.CreateClient().GetJsonAsync($"/api/v1/courses/{PaidCourse}");
        var checkout = await buyer.PostJsonAsync("/api/v1/checkout/sessions", new { courseId = course["id"]!.GetValue<string>(), referralCode = "FRIEND10" });
        var orderId = Guid.Parse(checkout["orderId"]!.GetValue<string>());

        await using (var db = _app.Service<TmsDbContext>())
        {
            var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId);
            var body = new JsonObject { ["id"] = "evt_" + Guid.NewGuid().ToString("N"), ["type"] = "payment.captured", ["orderId"] = order.ProviderOrderId, ["paymentId"] = "pay_" + Guid.NewGuid().ToString("N"), ["amount"] = order.TotalMinor }.ToJsonString();
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/sandbox") { Content = new StringContent(body) };
            req.Headers.Add(SandboxPaymentProvider.SignatureHeader, _app.Service<SandboxPaymentProvider>().Sign(body));
            (await _app.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();
        }

        var enrollment = (await buyer.GetJsonAsync("/api/v1/me/enrollments")).AsArray().Single()!;
        var player = await buyer.GetJsonAsync($"/api/v1/enrollments/{enrollment["id"]}");
        // First milestone of this course: finish module 1.
        foreach (var lesson in player["modules"]!.AsArray()[0]!["lessons"]!.AsArray())
            (await buyer.PutAsync($"/api/v1/enrollments/{enrollment["id"]}/lessons/{lesson!["id"]}/progress", null)).EnsureSuccessStatusCode();

        await using var check = _app.Service<TmsDbContext>();
        var referral = await check.CommissionEntries.AsNoTracking().Where(e => e.OrderId == orderId && e.RecipientKind == RecipientKind.Referrer).ToListAsync();
        Assert.Equal(CommissionStatus.Pending, referral.Single(e => e.ReleaseStage == ReferralStage.FirstMilestone).Status);
        Assert.NotNull(referral.Single(e => e.ReleaseStage == ReferralStage.FirstMilestone).ReleasedAt);
        Assert.Equal(CommissionStatus.OnHold, referral.Single(e => e.ReleaseStage == ReferralStage.CourseCompleted).Status);
    }

    [Fact]
    public async Task Finishing_every_step_of_the_FDE_path_awards_the_career_credential()
    {
        var (learner, email) = await NewLearnerAsync();
        var list = (await _app.CreateClient().GetJsonAsync("/api/v1/careers")).AsArray();
        var fde = list.Single(p => p!["slug"]!.GetValue<string>() == "forward-deployed-engineer")!;
        Assert.Equal(4, fde["steps"]!.GetValue<int>());
        (await learner.PostAsync($"/api/v1/careers/{fde["id"]}/join", null)).EnsureSuccessStatusCode();

        var detail = await learner.GetJsonAsync("/api/v1/careers/forward-deployed-engineer");
        Assert.All(detail["steps"]!.AsArray(), s => Assert.False(s!["progress"]!["achieved"]!.GetValue<bool>()));

        // Simulate the learner completing the three courses and attending three classes.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            var path = await db.CareerPaths.Include(p => p.Steps).FirstAsync(p => p.Slug == "forward-deployed-engineer");
            var courseIds = path.Steps.Where(s => s.CourseId != null).Select(s => s.CourseId!.Value).ToList();
            foreach (var cid in courseIds)
            {
                var course = await db.Courses.FirstAsync(c => c.Id == cid);
                db.Certificates.Add(new Certificate
                {
                    CredentialCode = $"T-{Guid.NewGuid():N}", UserId = user.Id, CourseId = cid, CourseVersionId = course.PublishedVersionId!.Value,
                    EnrollmentId = Guid.NewGuid(), IssuerTenantId = course.TenantId, RecipientName = user.FullName, CourseTitle = course.Title,
                    IssuerName = "Test", Status = CertificateStatus.Active,
                });
            }
            for (var i = 0; i < 3; i++)
            {
                var session = new ClassSession { CourseId = courseIds[0], Title = $"T{i}", StartsAt = DateTime.UtcNow.AddDays(-i - 1), EndsAt = DateTime.UtcNow.AddDays(-i - 1).AddHours(1), Capacity = 5, InstructorId = user.Id, Status = SessionStatus.Ended };
                db.ClassSessions.Add(session);
                db.SessionAttendance.Add(new SessionAttendance { SessionId = session.Id, UserId = user.Id, Status = AttendanceStatus.Present });
            }
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<CareerService>().RefreshAsync(user.Id);
        }

        var done = await learner.GetJsonAsync("/api/v1/careers/forward-deployed-engineer");
        Assert.All(done["steps"]!.AsArray(), s => Assert.True(s!["progress"]!["achieved"]!.GetValue<bool>()));
        Assert.StartsWith("FDE-", done["enrollment"]!["credentialCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_platform_staff_manage_career_paths()
    {
        var (learner, _) = await NewLearnerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.PostAsJsonAsync("/api/v1/careers", new { title = "X", role = "Y" })).StatusCode);

        var admin = await _app.LoginAsync("admin@tms.local");
        var created = await admin.PostJsonAsync("/api/v1/careers", new { title = "Become a Data Analyst", role = "Data Analyst", credentialPrefix = "DA" });
        var step = await admin.PostJsonAsync($"/api/v1/careers/{created["id"]}/steps", new { kind = "AttendClasses", requiredCount = 2 });
        Assert.NotNull(step["id"]);
        var detail = await admin.GetJsonAsync($"/api/v1/careers/{created["slug"]}");
        Assert.Equal("Attend 2 live classes", detail["steps"]!.AsArray().Single()!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Recommendations_use_the_AI_pick_and_fall_back_to_interest_matching()
    {
        var (learner, _) = await NewLearnerAsync();
        await learner.PutAsJsonAsync("/api/v1/me/interests", new { interests = new[] { "Data & Analytics" } });
        var me = await learner.GetJsonAsync("/api/v1/me");
        Assert.Equal("Data & Analytics", me["interests"]!.AsArray().Single()!.GetValue<string>());

        // The AI's answer is unusable → interest matching still returns results.
        _app.Llm.Reply = "not json";
        var fallback = await learner.GetJsonAsync("/api/v1/me/recommendations");
        Assert.Equal("rules", fallback["source"]!.GetValue<string>());
        Assert.Contains(fallback["items"]!.AsArray(), i => i!["slug"]!.GetValue<string>() == PaidCourse);

        // A valid AI pick is used, and ids outside the catalog are dropped.
        var pathId = (await learner.GetJsonAsync("/api/v1/careers")).AsArray().First()!["id"]!.GetValue<string>();
        _app.Llm.Reply = new JsonObject
        {
            ["summary"] = "You're heading towards customer-facing engineering.",
            ["items"] = new JsonArray(
                new JsonObject { ["type"] = "path", ["id"] = pathId, ["reason"] = "It fits your interests." },
                new JsonObject { ["type"] = "course", ["id"] = Guid.NewGuid().ToString(), ["reason"] = "Invented." }),
        }.ToJsonString();
        var ai = await learner.GetJsonAsync("/api/v1/me/recommendations");
        Assert.Equal("gemini", ai["source"]!.GetValue<string>());
        Assert.Equal(pathId, ai["items"]!.AsArray().Single()!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Syllabus_assistant_returns_a_reviewable_plan_with_only_real_ids()
    {
        var prof = await _app.LoginAsync("prof@tms.local");
        var courseId = (await prof.GetJsonAsync("/api/v1/courses/customer-deployments"))["id"]!.GetValue<string>();
        var draft = await prof.GetJsonAsync($"/api/v1/courses/{courseId}/draft");
        var moduleId = draft["modules"]!.AsArray()[0]!["id"]!.GetValue<string>();

        _app.Llm.Reply = new JsonObject
        {
            ["learningOutcomes"] = new JsonArray("Plan a deployment"),
            ["prerequisites"] = new JsonArray("Command line"),
            ["modules"] = new JsonArray(new JsonObject { ["moduleId"] = moduleId, ["week"] = 1, ["objectives"] = new JsonArray("Run discovery") },
                new JsonObject { ["moduleId"] = Guid.NewGuid().ToString(), ["week"] = 2, ["objectives"] = new JsonArray("Ghost") }),
            ["milestones"] = new JsonArray(
                new JsonObject { ["title"] = "Plan ready", ["description"] = "", ["kind"] = "CompleteModule", ["moduleId"] = moduleId, ["assessmentId"] = "", ["requiredCount"] = 0, ["dueDays"] = 7 },
                new JsonObject { ["title"] = "Bogus", ["description"] = "", ["kind"] = "PassAssessment", ["moduleId"] = "", ["assessmentId"] = Guid.NewGuid().ToString(), ["requiredCount"] = 0, ["dueDays"] = 9 }),
        }.ToJsonString();

        var plan = await prof.PostJsonAsync($"/api/v1/courses/{courseId}/syllabus-draft", new { });
        Assert.Equal("Plan a deployment", plan["learningOutcomes"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(moduleId, plan["modules"]!.AsArray().Single()!["moduleId"]!.GetValue<string>());
        Assert.Equal("Plan ready", plan["milestones"]!.AsArray().Single()!["title"]!.GetValue<string>());

        var (learner, _) = await NewLearnerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.PostAsJsonAsync($"/api/v1/courses/{courseId}/syllabus-draft", new { })).StatusCode);
    }
}
