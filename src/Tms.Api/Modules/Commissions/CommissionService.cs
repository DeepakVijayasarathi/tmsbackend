using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Commerce;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Commissions;

public record Recipient(RecipientKind Kind, Guid? UserId, Guid? TenantId);

public record BalanceSummary(long Pending, long Approved, long Payable, long Paid, long OnHold, string Currency);

public class CommissionService(TmsDbContext db, IConfiguration config)
{
    private long PayoutMinimum => config.GetValue("Commerce:PayoutMinimumMinor", 0L);

    public async Task<CommissionPlan> ResolvePlanAsync(Guid courseId, Guid tenantId, DateTime at)
    {
        var candidates = await db.CommissionPlans
            .Where(p => p.Status == PlanStatus.Active && p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at))
            .Where(p => p.CourseId == courseId || (p.CourseId == null && (p.TenantId == tenantId || p.TenantId == null)))
            .ToListAsync();

        return candidates.FirstOrDefault(p => p.CourseId == courseId)
            ?? candidates.FirstOrDefault(p => p.CourseId == null && p.TenantId == tenantId)
            ?? candidates.FirstOrDefault(p => p.CourseId == null && p.TenantId == null)
            ?? throw new InvalidOperationException("No active commission plan applies — configure a platform default plan.");
    }

    /// <summary>Posts accrual entries for a newly paid order. Caller saves inside the fulfilment transaction.</summary>
    public async Task AccrueAsync(Order order, Guid? referrerUserId)
    {
        var now = DateTime.UtcNow;
        foreach (var item in order.Items)
        {
            var tenant = await db.Tenants.FindAsync(item.CourseTenantId)
                ?? throw new InvalidOperationException("Course tenant missing.");
            var plan = await ResolvePlanAsync(item.CourseId, item.CourseTenantId, order.PaidAt ?? now);
            var baseMinor = item.UnitPriceMinor - item.TaxMinor;

            var sale = new SaleContext(baseMinor, order.BuyerUserId, item.InstructorId, item.CourseTenantId,
                tenant.Kind == TenantKind.College, referrerUserId);

            foreach (var a in CommissionCalculator.Allocate(sale, plan))
            {
                // Referral shares may be released in stages as the referred learner progresses.
                var tranches = a.Kind == RecipientKind.Referrer
                    ? CommissionCalculator.SplitReferral(a.AmountMinor, plan.ReferralSchedule)
                    : [(ReferralStage.Purchase, 100, a.AmountMinor)];
                foreach (var (stage, percent, amount) in tranches)
                db.CommissionEntries.Add(new CommissionEntry
                {
                    OrderId = order.Id,
                    OrderItemId = item.Id,
                    RecipientKind = a.Kind,
                    RecipientUserId = a.UserId,
                    RecipientTenantId = a.TenantId,
                    EntryType = CommissionEntryType.Accrual,
                    BaseMinor = baseMinor,
                    RateBp = a.RateBp,
                    AmountMinor = amount,
                    Currency = order.Currency,
                    PlanId = plan.Id,
                    PlanVersion = plan.Version,
                    Calculation = JsonSerializer.Serialize(new
                    {
                        basis = "net of tax",
                        gross = item.UnitPriceMinor,
                        tax = item.TaxMinor,
                        baseMinor,
                        rateBp = a.RateBp,
                        plan = plan.Name,
                        planVersion = plan.Version,
                        note = a.Note,
                        stage = tranches.Count > 1 ? $"{percent}% released at {stage}" : null,
                    }),
                    // Platform revenue is not a liability, so it skips the payout workflow.
                    Status = a.Kind == RecipientKind.Platform ? CommissionStatus.Approved
                        : stage == ReferralStage.Purchase ? CommissionStatus.Pending : CommissionStatus.OnHold,
                    ReleaseStage = tranches.Count > 1 ? stage : null,
                    ReleasedAt = stage == ReferralStage.Purchase ? now : null,
                    AvailableAt = now.AddDays(plan.HoldDays),
                });
            }
        }
    }

    /// <summary>Posts reversal entries after a refund. Never edits posted amounts.</summary>
    public async Task ReverseAsync(Order order, Guid? actor)
    {
        var entries = await db.CommissionEntries.Where(e => e.OrderId == order.Id).ToListAsync();
        var accruals = entries.Where(e => e.EntryType == CommissionEntryType.Accrual).ToList();

        foreach (var accrual in accruals)
        {
            var alreadyReversed = -entries.Where(e => e.ReversesEntryId == accrual.Id).Sum(e => e.AmountMinor);
            var amount = CommissionCalculator.ReversalAmount(accrual.AmountMinor, alreadyReversed, order.RefundedMinor, order.TotalMinor);
            if (amount == 0) continue;

            var fullyReversed = alreadyReversed + amount == accrual.AmountMinor;
            var unpaid = accrual.Status is not CommissionStatus.Paid && accrual.PayoutId is null;

            CommissionStatus reversalStatus;
            if (accrual.RecipientKind == RecipientKind.Platform) reversalStatus = CommissionStatus.Approved;
            else if (!unpaid) reversalStatus = CommissionStatus.Payable; // claw back against the next payout
            else if (fullyReversed) reversalStatus = CommissionStatus.Reversed;
            else reversalStatus = accrual.Status;

            var reversal = new CommissionEntry
            {
                OrderId = accrual.OrderId,
                OrderItemId = accrual.OrderItemId,
                RecipientKind = accrual.RecipientKind,
                RecipientUserId = accrual.RecipientUserId,
                RecipientTenantId = accrual.RecipientTenantId,
                EntryType = CommissionEntryType.Reversal,
                ReversesEntryId = accrual.Id,
                BaseMinor = accrual.BaseMinor,
                RateBp = accrual.RateBp,
                AmountMinor = -amount,
                Currency = accrual.Currency,
                PlanId = accrual.PlanId,
                PlanVersion = accrual.PlanVersion,
                Calculation = JsonSerializer.Serialize(new
                {
                    reason = "refund",
                    refundedMinor = order.RefundedMinor,
                    orderTotalMinor = order.TotalMinor,
                    originalAmount = accrual.AmountMinor,
                    alreadyReversed,
                }),
                Status = reversalStatus,
                AvailableAt = DateTime.UtcNow,
            };
            db.CommissionEntries.Add(reversal);

            if (unpaid && fullyReversed && accrual.RecipientKind != RecipientKind.Platform)
            {
                accrual.Status = CommissionStatus.Reversed;
                // Earlier partial reversals of this entry are now netted by the full reversal.
                foreach (var prior in entries.Where(e => e.ReversesEntryId == accrual.Id))
                    prior.Status = CommissionStatus.Reversed;
            }
        }
        Audit.Record(db, actor, "commission.reversed", nameof(Order), order.Id, new { order.RefundedMinor });
    }

    public async Task<int> ApproveAsync(IEnumerable<Guid> entryIds, Guid approver)
    {
        var ids = entryIds.ToList();
        var now = DateTime.UtcNow;
        var entries = await db.CommissionEntries
            .Where(e => ids.Contains(e.Id) && e.Status == CommissionStatus.Pending)
            .ToListAsync();
        foreach (var e in entries)
        {
            e.Status = e.AvailableAt <= now ? CommissionStatus.Payable : CommissionStatus.Approved;
            e.ApprovedAt = now;
            e.ApprovedBy = approver;
        }
        Audit.Record(db, approver, "commission.approved", nameof(CommissionEntry), null, new { ids = entries.Select(e => e.Id) });
        await db.SaveChangesAsync();
        return entries.Count;
    }

    /// <summary>Moves approved earnings whose holding period has ended to payable.</summary>
    public async Task<int> PromoteMaturedAsync()
    {
        var now = DateTime.UtcNow;
        var due = await db.CommissionEntries
            .Where(e => e.Status == CommissionStatus.Approved && e.RecipientKind != RecipientKind.Platform && e.AvailableAt <= now)
            .ToListAsync();
        foreach (var e in due) e.Status = CommissionStatus.Payable;
        await db.SaveChangesAsync();
        return due.Count;
    }

    public IQueryable<CommissionEntry> ForRecipient(Recipient r) =>
        db.CommissionEntries.Where(e => e.RecipientKind == r.Kind
            && (r.UserId == null || e.RecipientUserId == r.UserId)
            && (r.TenantId == null || e.RecipientTenantId == r.TenantId));

    public async Task<BalanceSummary> BalanceAsync(IQueryable<CommissionEntry> entries, string currency)
    {
        var sums = await entries.GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Sum = g.Sum(e => e.AmountMinor) })
            .ToListAsync();
        long Get(CommissionStatus s) => sums.FirstOrDefault(x => x.Status == s)?.Sum ?? 0;
        return new BalanceSummary(Get(CommissionStatus.Pending), Get(CommissionStatus.Approved),
            Get(CommissionStatus.Payable), Get(CommissionStatus.Paid), Get(CommissionStatus.OnHold), currency);
    }

    /// <summary>Idempotent: the same key always returns the same payout.</summary>
    public async Task<Payout> RequestPayoutAsync(Recipient r, Guid requestedBy, string idempotencyKey)
    {
        var existing = await db.Payouts.FirstOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey);
        if (existing is not null)
        {
            if (existing.RequestedBy != requestedBy) throw AppException.Conflict("Idempotency key already used.");
            return existing;
        }

        if (await db.Payouts.AnyAsync(p => p.RecipientKind == r.Kind && p.RecipientUserId == r.UserId
                && p.RecipientTenantId == r.TenantId && (p.Status == PayoutStatus.Requested || p.Status == PayoutStatus.Approved)))
            throw AppException.Conflict("A payout is already in progress for this account.");

        var entries = await ForRecipient(r).Where(e => e.Status == CommissionStatus.Payable && e.PayoutId == null).ToListAsync();
        var total = entries.Sum(e => e.AmountMinor);
        if (total <= 0) throw AppException.Invalid("There is no payable balance.");
        if (total < PayoutMinimum)
            throw AppException.Invalid($"Payable balance is below the payout minimum of {PayoutMinimum / 100m:0.00}.");

        var payout = new Payout
        {
            RecipientKind = r.Kind,
            RecipientUserId = r.UserId,
            RecipientTenantId = r.TenantId,
            AmountMinor = total,
            Currency = entries[0].Currency,
            Status = PayoutStatus.Requested,
            IdempotencyKey = idempotencyKey,
            RequestedBy = requestedBy,
        };
        db.Payouts.Add(payout);
        foreach (var e in entries) e.PayoutId = payout.Id;
        Audit.Record(db, requestedBy, "payout.requested", nameof(Payout), payout.Id, new { total, entries = entries.Count });
        await db.SaveChangesAsync();
        return payout;
    }

    public async Task<Payout> DecidePayoutAsync(Guid payoutId, Guid actor, bool approve)
    {
        var payout = await db.Payouts.FindAsync(payoutId) ?? throw AppException.NotFound("Payout");
        if (payout.Status != PayoutStatus.Requested) throw AppException.Conflict($"Payout is {payout.Status}.");
        if (payout.RequestedBy == actor) throw AppException.Forbidden("Payouts need approval from someone other than the requester.");

        if (approve)
        {
            payout.Status = PayoutStatus.Approved;
            payout.ApprovedBy = actor;
            payout.ApprovedAt = DateTime.UtcNow;
        }
        else
        {
            payout.Status = PayoutStatus.Rejected;
            await db.CommissionEntries.Where(e => e.PayoutId == payout.Id)
                .ForEachAsync(e => e.PayoutId = null);
        }
        Audit.Record(db, actor, approve ? "payout.approved" : "payout.rejected", nameof(Payout), payout.Id);
        await db.SaveChangesAsync();
        return payout;
    }

    public async Task<Payout> MarkPaidAsync(Guid payoutId, Guid actor, string reference)
    {
        var payout = await db.Payouts.FindAsync(payoutId) ?? throw AppException.NotFound("Payout");
        if (payout.Status == PayoutStatus.Paid) return payout;
        if (payout.Status != PayoutStatus.Approved) throw AppException.Conflict("Only approved payouts can be marked paid.");

        payout.Status = PayoutStatus.Paid;
        payout.PaidAt = DateTime.UtcNow;
        payout.Reference = reference;
        await db.CommissionEntries.Where(e => e.PayoutId == payout.Id)
            .ForEachAsync(e => e.Status = CommissionStatus.Paid);
        Audit.Record(db, actor, "payout.paid", nameof(Payout), payout.Id, new { reference });
        await db.SaveChangesAsync();
        return payout;
    }
}

/// <summary>Background job that releases held earnings once their holding period ends.</summary>
public class CommissionMaturityWorker(IServiceScopeFactory scopes, ILogger<CommissionMaturityWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var promoted = await scope.ServiceProvider.GetRequiredService<CommissionService>().PromoteMaturedAsync();
                if (promoted > 0) log.LogInformation("Released {Count} commission entries to payable", promoted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Commission maturity run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
