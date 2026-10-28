using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Data;
using Tms.Api.Modules.Commerce.Providers;
using Tms.Api.Modules.Commissions;

namespace Tms.Tests;

public class PlatformFlowTests : IClassFixture<TmsFactory>
{
    private const string PaidCourse = "practical-data-analysis-with-python";
    private readonly TmsFactory _app;

    public PlatformFlowTests(TmsFactory app) => _app = app;

    private async Task<(HttpClient Buyer, string OrderId)> NewBuyerOrderAsync(string? referral = null)
    {
        var anon = _app.CreateClient();
        var email = $"buyer-{Guid.NewGuid():N}@test.local";
        var reg = await anon.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Passw0rd!", fullName = "Test Buyer", accountType = "learner" });
        reg.EnsureSuccessStatusCode();
        var buyer = await _app.LoginAsync(email);
        var course = await anon.GetJsonAsync($"/api/v1/courses/{PaidCourse}");
        var checkout = await buyer.PostJsonAsync("/api/v1/checkout/sessions", new { courseId = course["id"]!.GetValue<string>(), referralCode = referral });
        return (buyer, checkout["orderId"]!.GetValue<string>());
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(string body, string? signature = null)
    {
        var sandbox = _app.Service<SandboxPaymentProvider>();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/sandbox") { Content = new StringContent(body) };
        req.Headers.Add(SandboxPaymentProvider.SignatureHeader, signature ?? sandbox.Sign(body));
        return await _app.CreateClient().SendAsync(req);
    }

    private async Task<string> CaptureBodyAsync(string orderId, long? amount = null)
    {
        await using var db = _app.Service<TmsDbContext>();
        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == Guid.Parse(orderId));
        return new JsonObject
        {
            ["id"] = "evt_" + Guid.NewGuid().ToString("N"),
            ["type"] = "payment.captured",
            ["orderId"] = order.ProviderOrderId,
            ["paymentId"] = "pay_" + Guid.NewGuid().ToString("N"),
            ["amount"] = amount ?? order.TotalMinor,
        }.ToJsonString();
    }

    [Fact]
    public async Task Duplicate_webhooks_are_acknowledged_but_applied_once()
    {
        var (buyer, orderId) = await NewBuyerOrderAsync("FRIEND10");
        var body = await CaptureBodyAsync(orderId);

        var first = await (await SendWebhookAsync(body)).ReadAsync();
        var second = await (await SendWebhookAsync(body)).ReadAsync();

        Assert.Equal("fulfilled", first["outcome"]!.GetValue<string>());
        Assert.Equal("duplicate", second["outcome"]!.GetValue<string>());

        await using var db = _app.Service<TmsDbContext>();
        var id = Guid.Parse(orderId);
        Assert.Equal(1, await db.Payments.CountAsync(p => p.OrderId == id));
        Assert.Equal(1, await db.Enrollments.CountAsync(e => e.OrderId == id));
        var entries = await db.CommissionEntries.Where(e => e.OrderId == id).ToListAsync();
        Assert.Equal(4, entries.Select(e => e.RecipientKind).Distinct().Count()); // professor, college, referrer, platform
        // The referral share is staged: 30% now, the rest held until the learner progresses.
        var referral = entries.Where(e => e.RecipientKind == RecipientKind.Referrer).ToList();
        Assert.Equal(3, referral.Count);
        Assert.Single(referral, e => e.Status == CommissionStatus.Pending);
        Assert.Equal(2, referral.Count(e => e.Status == CommissionStatus.OnHold));
        var order = await db.Orders.FirstAsync(o => o.Id == id);
        Assert.Equal(order.TotalMinor - order.TaxMinor, entries.Sum(e => e.AmountMinor));
    }

    [Fact]
    public async Task Webhooks_with_a_bad_signature_are_rejected_and_not_stored()
    {
        var (_, orderId) = await NewBuyerOrderAsync();
        var body = await CaptureBodyAsync(orderId);

        var res = await SendWebhookAsync(body, signature: new string('0', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        await using var db = _app.Service<TmsDbContext>();
        Assert.Equal("Pending", (await db.Orders.FirstAsync(o => o.Id == Guid.Parse(orderId))).Status.ToString());
    }

    [Fact]
    public async Task Underpaid_capture_does_not_fulfil_the_order()
    {
        var (buyer, orderId) = await NewBuyerOrderAsync();
        var res = await (await SendWebhookAsync(await CaptureBodyAsync(orderId, amount: 1))).ReadAsync();

        Assert.StartsWith("amount mismatch", res["outcome"]!.GetValue<string>());
        var order = await buyer.GetJsonAsync($"/api/v1/orders/{orderId}");
        Assert.Equal("Pending", order["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Learners_never_receive_answer_keys_before_submitting()
    {
        var learner = await _app.LoginAsync("learner@tms.local");
        var course = await learner.GetJsonAsync("/api/v1/courses/workplace-communication-essentials");
        var enrol = await learner.PostJsonAsync($"/api/v1/courses/{course["id"]}/enrollments", new { });
        var player = await learner.GetJsonAsync($"/api/v1/enrollments/{enrol["id"]}");
        var assessmentId = player["assessments"]![0]!["id"]!.GetValue<string>();

        var attemptRes = await learner.PostAsync($"/api/v1/assessments/{assessmentId}/attempts", null);
        var raw = await attemptRes.Content.ReadAsStringAsync();

        Assert.True(attemptRes.IsSuccessStatusCode, raw);
        Assert.DoesNotContain("isCorrect", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explanation", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Users_cannot_read_another_tenants_data()
    {
        var learner = await _app.LoginAsync("learner@tms.local");
        var collegeAdmin = await _app.LoginAsync("college@tms.local");
        var collegeId = (await collegeAdmin.GetJsonAsync("/api/v1/me"))["tenants"]![0]!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await learner.GetAsync($"/api/v1/tenants/{collegeId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.GetAsync($"/api/v1/colleges/{collegeId}/commissions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collegeAdmin.GetAsync("/api/v1/analytics/platform")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await collegeAdmin.GetAsync($"/api/v1/tenants/{collegeId}/members")).StatusCode);
    }

    [Fact]
    public async Task Refund_after_payout_creates_a_clawback_instead_of_editing_history()
    {
        var (_, orderId) = await NewBuyerOrderAsync();
        await (await SendWebhookAsync(await CaptureBodyAsync(orderId))).ReadAsync();
        var finance = await _app.LoginAsync("finance@tms.local");
        var admin = await _app.LoginAsync("admin@tms.local");
        var prof = await _app.LoginAsync("prof@tms.local");

        var pending = (await finance.GetJsonAsync("/api/v1/commissions/entries?status=Pending")).AsArray()
            .Select(e => e!["id"]!.GetValue<string>()).ToList();
        await finance.PostJsonAsync("/api/v1/commissions/approve", new { entryIds = pending });

        var payout = await prof.PostJsonAsync("/api/v1/payouts", new { recipientKind = "Professor", idempotencyKey = $"k-{Guid.NewGuid()}" });
        var payoutId = payout["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Forbidden, (await prof.PostAsync($"/api/v1/payouts/{payoutId}/approve", null)).StatusCode);
        await finance.PostJsonAsync($"/api/v1/payouts/{payoutId}/approve", new { });
        await finance.PostJsonAsync($"/api/v1/payouts/{payoutId}/mark-paid", new { reference = "UTR123" });

        await using var db = _app.Service<TmsDbContext>();
        var id = Guid.Parse(orderId);
        var professorAccrual = await db.CommissionEntries.AsNoTracking()
            .FirstAsync(e => e.OrderId == id && e.RecipientKind == RecipientKind.Professor);
        Assert.Equal(CommissionStatus.Paid, professorAccrual.Status);

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id);
        await admin.PostJsonAsync($"/api/v1/admin/orders/{orderId}/refunds", new { amountMinor = order.TotalMinor, reason = "test" });

        db.ChangeTracker.Clear();
        var after = await db.CommissionEntries.AsNoTracking().Where(e => e.OrderId == id && e.RecipientKind == RecipientKind.Professor).ToListAsync();
        var accrual = after.Single(e => e.EntryType == CommissionEntryType.Accrual);
        var clawback = after.Single(e => e.EntryType == CommissionEntryType.Reversal);
        Assert.Equal(professorAccrual.AmountMinor, accrual.AmountMinor); // posted amount untouched
        Assert.Equal(CommissionStatus.Paid, accrual.Status);
        Assert.Equal(-accrual.AmountMinor, clawback.AmountMinor);
        Assert.Equal(CommissionStatus.Payable, clawback.Status);    // netted against the next payout
        Assert.Equal("Revoked", (await db.Enrollments.FirstAsync(e => e.OrderId == id)).Status.ToString());
    }

    [Fact]
    public async Task Payout_requests_are_idempotent()
    {
        var (_, orderId) = await NewBuyerOrderAsync("FRIEND10");
        await (await SendWebhookAsync(await CaptureBodyAsync(orderId))).ReadAsync();
        var finance = await _app.LoginAsync("finance@tms.local");
        var pending = (await finance.GetJsonAsync("/api/v1/commissions/entries?status=Pending&kind=Referrer")).AsArray()
            .Select(e => e!["id"]!.GetValue<string>()).ToList();
        await finance.PostJsonAsync("/api/v1/commissions/approve", new { entryIds = pending });

        var affiliate = await _app.LoginAsync("affiliate@tms.local");
        var key = $"aff-{Guid.NewGuid()}";
        var a = await affiliate.PostJsonAsync("/api/v1/payouts", new { recipientKind = "Referrer", idempotencyKey = key });
        var b = await affiliate.PostJsonAsync("/api/v1/payouts", new { recipientKind = "Referrer", idempotencyKey = key });
        Assert.Equal(a["id"]!.GetValue<string>(), b["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ai_tutor_requires_enrollment_and_sends_only_lesson_material()
    {
        var outsider = _app.CreateClient();
        var email = $"outsider-{Guid.NewGuid():N}@test.local";
        (await outsider.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Passw0rd!", fullName = "Outsider", accountType = "learner" })).EnsureSuccessStatusCode();
        outsider = await _app.LoginAsync(email);
        var course = await outsider.GetJsonAsync($"/api/v1/courses/{PaidCourse}");
        var courseId = course["id"]!.GetValue<string>();

        var denied = await outsider.PostAsJsonAsync("/api/v1/ai/chat", new { courseId, message = "What is NaN?" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var prof = await _app.LoginAsync("prof@tms.local");
        var answer = await prof.PostJsonAsync("/api/v1/ai/chat", new { courseId, message = "How should I handle missing values?" });
        Assert.NotEmpty(answer["sources"]!.AsArray());

        var sent = _app.Llm.Calls.Last().Turns.Last().Content;
        Assert.Contains("<course_material>", sent);
        Assert.Contains("dropna", sent);
        Assert.DoesNotContain("Filling missing amounts with the column mean is usually the safest default", sent); // an assessment question
    }
}
