using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Commerce.Providers;
using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;
using Tms.Api.Modules.Referrals;

namespace Tms.Api.Modules.Commerce;

public record WebhookResult(string Outcome, Guid? OrderId);

public class CommerceService(
    TmsDbContext db,
    PaymentProviderRegistry providers,
    CommissionService commissions,
    EnrollmentService enrollments,
    IConfiguration config,
    ILogger<CommerceService> log)
{
    private decimal TaxRate => config.GetValue("Commerce:TaxRatePercent", 0m);

    public async Task<(Order Order, CheckoutStart Start)> StartCheckoutAsync(Guid buyerId, Guid courseId, string? referralCode, CancellationToken ct)
    {
        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == courseId, ct) ?? throw AppException.NotFound("Course");
        if (course.Status != CourseStatus.Published || course.PublishedVersionId is null)
            throw AppException.Invalid("This course is not available for purchase.");
        if (course.PriceMinor <= 0) throw AppException.Invalid("This course is free — enrol directly.");
        if (await db.Enrollments.AnyAsync(e => e.UserId == buyerId && e.CourseId == courseId && e.Status != EnrollmentStatus.Revoked, ct))
            throw AppException.Conflict("You are already enrolled in this course.");

        ReferralCode? code = null;
        if (!string.IsNullOrWhiteSpace(referralCode))
            code = await db.ReferralCodes.FirstOrDefaultAsync(r => r.Code == referralCode.Trim().ToUpper() && r.IsActive, ct);

        var provider = providers.Default;
        var tax = CommissionCalculator.InclusiveTax(course.PriceMinor, TaxRate);
        var order = new Order
        {
            BuyerUserId = buyerId,
            Status = OrderStatus.Pending,
            Currency = course.Currency,
            SubtotalMinor = course.PriceMinor,
            TaxMinor = tax,
            TotalMinor = course.PriceMinor,
            Provider = provider.Name,
            ReferralCodeId = code?.Id,
            Items =
            [
                new OrderItem
                {
                    CourseId = course.Id,
                    CourseTitle = course.Title,
                    CourseTenantId = course.TenantId,
                    InstructorId = course.InstructorId,
                    UnitPriceMinor = course.PriceMinor,
                    TaxMinor = tax,
                },
            ],
        };

        var start = await provider.StartCheckoutAsync(order, ct);
        order.ProviderOrderId = start.ProviderOrderId;
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return (order, start);
    }

    /// <summary>
    /// Single entry point for provider events. Signature is verified before anything is stored,
    /// and (provider, event id) is unique, so redelivered events are acknowledged but never re-applied.
    /// </summary>
    public async Task<WebhookResult> HandleWebhookAsync(string providerName, IHeaderDictionary headers, string rawBody, CancellationToken ct)
    {
        var provider = providers.Get(providerName);
        if (!provider.VerifySignature(headers, rawBody))
            throw new AppException(StatusCodes.Status401Unauthorized, "Invalid webhook signature.");

        var evt = provider.Parse(headers, rawBody);
        if (await db.PaymentWebhookEvents.AnyAsync(e => e.Provider == provider.Name && e.EventId == evt.EventId, ct))
            return new WebhookResult("duplicate", null);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var record = new PaymentWebhookEvent { Provider = provider.Name, EventId = evt.EventId, EventType = evt.EventType, Payload = rawBody };
        db.PaymentWebhookEvents.Add(record);

        var order = evt.ProviderOrderId is null ? null
            : await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Provider == provider.Name && o.ProviderOrderId == evt.ProviderOrderId, ct);

        record.Outcome = (evt.Kind, order) switch
        {
            (_, null) when evt.Kind != PaymentEventKind.Ignored => "unknown order",
            (PaymentEventKind.Captured, { } o) => await FulfilAsync(o, provider.Name, evt),
            (PaymentEventKind.Failed, { Status: OrderStatus.Pending } o) => MarkFailed(o),
            _ => "ignored",
        };
        record.ProcessedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            // A concurrent delivery of the same event won the race; anything else is a real failure.
            if (await IsDuplicateAsync(provider.Name, evt.EventId)) return new WebhookResult("duplicate", order?.Id);
            throw;
        }

        log.LogInformation("Webhook {Provider}/{EventId} {Type}: {Outcome}", provider.Name, evt.EventId, evt.EventType, record.Outcome);
        return new WebhookResult(record.Outcome!, order?.Id);
    }

    private async Task<bool> IsDuplicateAsync(string provider, string eventId)
    {
        db.ChangeTracker.Clear();
        return await db.PaymentWebhookEvents.AnyAsync(e => e.Provider == provider && e.EventId == eventId);
    }

    private static string MarkFailed(Order order)
    {
        order.Status = OrderStatus.Failed;
        return "order failed";
    }

    private async Task<string> FulfilAsync(Order order, string provider, PaymentEvent evt)
    {
        if (order.Status != OrderStatus.Pending && order.Status != OrderStatus.Failed) return "already fulfilled";
        if (evt.AmountMinor != order.TotalMinor) return $"amount mismatch (expected {order.TotalMinor}, got {evt.AmountMinor})";

        order.Status = OrderStatus.Paid;
        order.PaidAt = DateTime.UtcNow;
        db.Payments.Add(new Payment
        {
            OrderId = order.Id,
            Provider = provider,
            ProviderPaymentId = evt.ProviderPaymentId ?? evt.EventId,
            AmountMinor = evt.AmountMinor,
            Status = PaymentStatus.Captured,
        });

        foreach (var item in order.Items)
        {
            var course = await db.Courses.FindAsync(item.CourseId);
            if (course is not null) await enrollments.EnrolAsync(order.BuyerUserId, course, EnrollmentSource.Purchase, order.Id);
        }

        var referrer = await CreditReferralAsync(order);
        await commissions.AccrueAsync(order, referrer);
        Audit.Record(db, order.BuyerUserId, "order.paid", nameof(Order), order.Id, new { order.TotalMinor, provider });
        return "fulfilled";
    }

    /// <summary>Last-touch attribution: the code captured at checkout. Returns the referrer if eligible.</summary>
    private async Task<Guid?> CreditReferralAsync(Order order)
    {
        if (order.ReferralCodeId is null) return null;
        var code = await db.ReferralCodes.FindAsync(order.ReferralCodeId);
        if (code is null) return null;

        var instructorIds = order.Items.Select(i => i.InstructorId).ToHashSet();
        string? rejection = code.OwnerUserId == order.BuyerUserId ? "self-referral"
            : !code.IsActive ? "code inactive"
            : null;

        db.ReferralConversions.Add(new ReferralConversion
        {
            ReferralCodeId = code.Id,
            ReferrerUserId = code.OwnerUserId,
            OrderId = order.Id,
            BuyerUserId = order.BuyerUserId,
            Status = rejection is null ? ReferralConversionStatus.Credited : ReferralConversionStatus.Rejected,
            Note = rejection ?? (instructorIds.Contains(code.OwnerUserId) ? "referrer is the instructor; plan stacking policy applies" : null),
        });
        // The calculator applies the stacking policy and self-referral guard again, so the ledger stays correct
        // even if this pre-check changes.
        return code.OwnerUserId;
    }

    public async Task<Refund> RefundAsync(Guid orderId, long amountMinor, string reason, Guid actor, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct) ?? throw AppException.NotFound("Order");
        if (order.Status is not (OrderStatus.Paid or OrderStatus.PartiallyRefunded))
            throw AppException.Conflict($"Order is {order.Status} and cannot be refunded.");
        var remaining = order.TotalMinor - order.RefundedMinor;
        if (amountMinor <= 0 || amountMinor > remaining)
            throw AppException.Invalid($"Refund must be between 0.01 and {remaining / 100m:0.00}.");

        var payment = await db.Payments.FirstAsync(p => p.OrderId == order.Id && p.Status == PaymentStatus.Captured, ct);
        var providerRefundId = await providers.Get(order.Provider).RefundAsync(order, payment, amountMinor, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var refund = new Refund
        {
            OrderId = order.Id,
            AmountMinor = amountMinor,
            Reason = reason,
            Status = RefundStatus.Processed,
            ProviderRefundId = providerRefundId,
            RequestedBy = actor,
        };
        db.Refunds.Add(refund);
        order.RefundedMinor += amountMinor;
        order.Status = order.RefundedMinor == order.TotalMinor ? OrderStatus.Refunded : OrderStatus.PartiallyRefunded;

        if (order.Status == OrderStatus.Refunded)
        {
            await db.Enrollments.Where(e => e.OrderId == order.Id).ForEachAsync(e => e.Status = EnrollmentStatus.Revoked, ct);
            await db.ReferralConversions.Where(r => r.OrderId == order.Id && r.Status == ReferralConversionStatus.Credited)
                .ForEachAsync(r => r.Status = ReferralConversionStatus.Reversed, ct);
        }

        await commissions.ReverseAsync(order, actor);
        Audit.Record(db, actor, "order.refunded", nameof(Order), order.Id, new { amountMinor, reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return refund;
    }
}
