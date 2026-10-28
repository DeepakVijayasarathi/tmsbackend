using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Commerce.Providers;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Commerce;

public record CheckoutRequest(Guid CourseId, string? ReferralCode);
public record RefundRequest(long AmountMinor, string Reason);

public static class CommerceEndpoints
{
    public static void MapCommerce(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Payments");

        g.MapPost("/checkout/sessions", async (CheckoutRequest req, CurrentUser me, CommerceService commerce, CancellationToken ct) =>
        {
            var (order, start) = await commerce.StartCheckoutAsync(me.Id, req.CourseId, req.ReferralCode, ct);
            return new { orderId = order.Id, provider = order.Provider, order.TotalMinor, order.Currency, start.RedirectUrl, start.ClientPayload };
        }).RequireAuthorization();

        g.MapGet("/orders/{orderId:guid}", async (Guid orderId, CurrentUser me, TmsDbContext db) =>
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw AppException.NotFound("Order");
            if (order.BuyerUserId != me.Id && !await me.HasPermissionAsync(Permissions.BillingManage))
                throw AppException.NotFound("Order");
            var enrollmentId = await db.Enrollments.Where(e => e.OrderId == order.Id).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
            return new
            {
                order.Id, order.Status, order.Currency, order.TotalMinor, order.TaxMinor, order.RefundedMinor, order.Provider, order.CreatedAt, order.PaidAt,
                Items = order.Items.Select(i => new { i.CourseId, i.CourseTitle, i.UnitPriceMinor }),
                EnrollmentId = enrollmentId,
            };
        }).RequireAuthorization();

        g.MapGet("/me/orders", async (CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            return await db.Orders.AsNoTracking().Where(o => o.BuyerUserId == userId).OrderByDescending(o => o.CreatedAt)
                .Select(o => new { o.Id, o.Status, o.TotalMinor, o.RefundedMinor, o.Currency, o.CreatedAt, Items = o.Items.Select(i => i.CourseTitle).ToList() })
                .ToListAsync();
        }).RequireAuthorization();

        // Provider webhooks: anonymous, authenticated by signature over the raw body.
        g.MapPost("/webhooks/payments/{provider}", async (string provider, HttpRequest request, CommerceService commerce, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var result = await commerce.HandleWebhookAsync(provider, request.Headers, body, ct);
            return Results.Ok(new { result.Outcome });
        }).DisableAntiforgery();

        // Development-only stand-in for a hosted checkout page: signs a capture event and sends it
        // through the same webhook handler a real provider would hit.
        g.MapPost("/dev/sandbox/orders/{orderId:guid}/pay", async (Guid orderId, CurrentUser me, TmsDbContext db,
            SandboxPaymentProvider sandbox, CommerceService commerce, CancellationToken ct) =>
        {
            if (!sandbox.Enabled) throw AppException.NotFound("Sandbox");
            var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.BuyerUserId == me.Id && o.Provider == sandbox.Name)
                ?? throw AppException.NotFound("Order");
            var body = SandboxEvents.Captured(order);
            var headers = new HeaderDictionary { [SandboxPaymentProvider.SignatureHeader] = sandbox.Sign(body) };
            var result = await commerce.HandleWebhookAsync(sandbox.Name, headers, body, ct);
            return new { result.Outcome };
        }).RequireAuthorization();

        var admin = api.MapGroup("/admin").WithTags("Billing admin").RequireAuthorization();

        admin.MapGet("/orders", async (OrderStatus? status, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.BillingManage);
            var q = db.Orders.AsNoTracking();
            if (status is not null) q = q.Where(o => o.Status == status);
            return await q.OrderByDescending(o => o.CreatedAt).Take(200).Select(o => new
            {
                o.Id, o.Status, o.TotalMinor, o.TaxMinor, o.RefundedMinor, o.Currency, o.Provider, o.CreatedAt, o.PaidAt,
                Buyer = db.Users.Where(u => u.Id == o.BuyerUserId).Select(u => u.Email).FirstOrDefault(),
                Items = o.Items.Select(i => i.CourseTitle).ToList(),
                Referral = db.ReferralCodes.Where(r => r.Id == o.ReferralCodeId).Select(r => r.Code).FirstOrDefault(),
            }).ToListAsync();
        });

        admin.MapPost("/orders/{orderId:guid}/refunds", async (Guid orderId, RefundRequest req, CurrentUser me, CommerceService commerce, CancellationToken ct) =>
        {
            await me.RequireAsync(Permissions.BillingManage);
            if (string.IsNullOrWhiteSpace(req.Reason)) throw AppException.Invalid("Give a reason for the refund.");
            var refund = await commerce.RefundAsync(orderId, req.AmountMinor, req.Reason.Trim(), me.Id, ct);
            return new { refund.Id, refund.Status, refund.AmountMinor };
        });

        admin.MapGet("/webhook-events", async (CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.BillingManage);
            return await db.PaymentWebhookEvents.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(100)
                .Select(e => new { e.Id, e.Provider, e.EventId, e.EventType, e.Outcome, e.CreatedAt, e.ProcessedAt }).ToListAsync();
        });
    }
}
