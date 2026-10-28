using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Commissions;

public record PlanRequest(Guid? PlanKey, string Name, Guid? TenantId, Guid? CourseId,
    int ProfessorBp, int CollegeBp, int ReferralBp, StackingPolicy Stacking, int HoldDays);
public record ApproveRequest(List<Guid> EntryIds);
public record PayoutRequest(RecipientKind RecipientKind, Guid? TenantId, string IdempotencyKey);
public record MarkPaidRequest(string Reference);

public static class CommissionEndpoints
{
    public static void MapCommissions(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Commissions").RequireAuthorization();

        g.MapGet("/commissions/plans", async (CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CommissionConfigure);
            return await db.CommissionPlans.AsNoTracking().OrderBy(p => p.Status).ThenBy(p => p.Name).ThenByDescending(p => p.Version)
                .Select(p => new
                {
                    p.Id, p.PlanKey, p.Version, p.Name, p.TenantId, p.CourseId, p.ProfessorBp, p.CollegeBp, p.ReferralBp,
                    p.Stacking, p.HoldDays, p.EffectiveFrom, p.EffectiveTo, p.Status,
                    Tenant = db.Tenants.Where(t => t.Id == p.TenantId).Select(t => t.Name).FirstOrDefault(),
                    Course = db.Courses.Where(c => c.Id == p.CourseId).Select(c => c.Title).FirstOrDefault(),
                }).ToListAsync();
        });

        // Changing a plan creates a new version; the old version stays for the entries calculated under it.
        g.MapPost("/commissions/plans", async (PlanRequest req, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CommissionConfigure);
            if (req.ProfessorBp < 0 || req.CollegeBp < 0 || req.ReferralBp < 0 || req.ProfessorBp + req.CollegeBp + req.ReferralBp > 10_000)
                throw AppException.Invalid("Rates must be non-negative and total at most 100%.");
            if (req.HoldDays is < 0 or > 365) throw AppException.Invalid("Hold period must be 0–365 days.");

            var now = DateTime.UtcNow;
            var plan = new CommissionPlan
            {
                Name = req.Name.Trim(), TenantId = req.TenantId, CourseId = req.CourseId,
                ProfessorBp = req.ProfessorBp, CollegeBp = req.CollegeBp, ReferralBp = req.ReferralBp,
                Stacking = req.Stacking, HoldDays = req.HoldDays, EffectiveFrom = now, Status = PlanStatus.Active,
            };

            if (req.PlanKey is { } key)
            {
                var current = await db.CommissionPlans.Where(p => p.PlanKey == key && p.Status == PlanStatus.Active).FirstOrDefaultAsync()
                    ?? throw AppException.NotFound("Active plan");
                current.Status = PlanStatus.Superseded;
                current.EffectiveTo = now;
                plan.PlanKey = key;
                plan.Version = current.Version + 1;
                plan.TenantId = current.TenantId;
                plan.CourseId = current.CourseId;
            }
            else if (await db.CommissionPlans.AnyAsync(p => p.Status == PlanStatus.Active && p.TenantId == req.TenantId && p.CourseId == req.CourseId))
            {
                throw AppException.Conflict("An active plan already covers this scope — edit it instead.");
            }

            db.CommissionPlans.Add(plan);
            Audit.Record(db, me.Id, "commission.plan_saved", nameof(CommissionPlan), plan.Id, req);
            await db.SaveChangesAsync();
            return new { plan.Id, plan.PlanKey, plan.Version };
        });

        g.MapGet("/commissions/entries", async (CommissionStatus? status, RecipientKind? kind, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CommissionApprove);
            var q = db.CommissionEntries.AsNoTracking().Where(e => e.RecipientKind != RecipientKind.Platform);
            if (status is not null) q = q.Where(e => e.Status == status);
            if (kind is not null) q = q.Where(e => e.RecipientKind == kind);
            return await q.OrderByDescending(e => e.CreatedAt).Take(300).Select(Project(db)).ToListAsync();
        });

        g.MapPost("/commissions/approve", async (ApproveRequest req, CurrentUser me, CommissionService commissions) =>
        {
            await me.RequireAsync(Permissions.CommissionApprove);
            return new { approved = await commissions.ApproveAsync(req.EntryIds, me.Id) };
        });

        g.MapGet("/professors/me/commissions", async (CurrentUser me, TmsDbContext db, CommissionService commissions, IConfiguration config) =>
        {
            var recipient = new Recipient(RecipientKind.Professor, me.Id, null);
            var entries = commissions.ForRecipient(recipient);
            return new
            {
                balance = await commissions.BalanceAsync(entries, config["Commerce:DefaultCurrency"] ?? "INR"),
                entries = await entries.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(200).Select(Project(db)).ToListAsync(),
            };
        });

        g.MapGet("/colleges/{tenantId:guid}/commissions", async (Guid tenantId, CurrentUser me, TmsDbContext db, CommissionService commissions, IConfiguration config) =>
        {
            await me.RequireAsync(Permissions.AnalyticsView, tenantId);
            var entries = commissions.ForRecipient(new Recipient(RecipientKind.College, null, tenantId));
            return new
            {
                balance = await commissions.BalanceAsync(entries, config["Commerce:DefaultCurrency"] ?? "INR"),
                entries = await entries.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(200).Select(Project(db)).ToListAsync(),
            };
        });

        g.MapGet("/payouts", async (bool? all, CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var q = db.Payouts.AsNoTracking();
            if (all == true) await me.RequireAsync(Permissions.PayoutApprove);
            else
            {
                var managed = (await me.MembershipsAsync()).Where(m => Permissions.For(m.Role).Contains(Permissions.MembersManage)).Select(m => m.TenantId).ToList();
                q = q.Where(p => p.RecipientUserId == userId || (p.RecipientTenantId != null && managed.Contains(p.RecipientTenantId.Value)));
            }
            return await q.OrderByDescending(p => p.CreatedAt).Take(200).Select(p => new
            {
                p.Id, p.RecipientKind, p.AmountMinor, p.Currency, p.Status, p.CreatedAt, p.ApprovedAt, p.PaidAt, p.Reference,
                Recipient = p.RecipientTenantId != null
                    ? db.Tenants.Where(t => t.Id == p.RecipientTenantId).Select(t => t.Name).FirstOrDefault()
                    : db.Users.Where(u => u.Id == p.RecipientUserId).Select(u => u.FullName).FirstOrDefault(),
                Entries = db.CommissionEntries.Count(e => e.PayoutId == p.Id),
            }).ToListAsync();
        });

        g.MapPost("/payouts", async (PayoutRequest req, CurrentUser me, CommissionService commissions) =>
        {
            if (string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 100)
                throw AppException.Invalid("An idempotency key is required.");
            Recipient recipient = req.RecipientKind switch
            {
                RecipientKind.Professor or RecipientKind.Referrer => new Recipient(req.RecipientKind, me.Id, null),
                RecipientKind.College when req.TenantId is { } t => new Recipient(RecipientKind.College, null, t),
                _ => throw AppException.Invalid("Unsupported payout recipient."),
            };
            if (recipient.TenantId is { } tenantId) await me.RequireAsync(Permissions.MembersManage, tenantId);
            var payout = await commissions.RequestPayoutAsync(recipient, me.Id, req.IdempotencyKey);
            return new { payout.Id, payout.Status, payout.AmountMinor };
        });

        g.MapPost("/payouts/{payoutId:guid}/approve", async (Guid payoutId, CurrentUser me, CommissionService commissions) =>
        {
            await me.RequireAsync(Permissions.PayoutApprove);
            var p = await commissions.DecidePayoutAsync(payoutId, me.Id, approve: true);
            return new { p.Id, p.Status };
        });

        g.MapPost("/payouts/{payoutId:guid}/reject", async (Guid payoutId, CurrentUser me, CommissionService commissions) =>
        {
            await me.RequireAsync(Permissions.PayoutApprove);
            var p = await commissions.DecidePayoutAsync(payoutId, me.Id, approve: false);
            return new { p.Id, p.Status };
        });

        g.MapPost("/payouts/{payoutId:guid}/mark-paid", async (Guid payoutId, MarkPaidRequest req, CurrentUser me, CommissionService commissions) =>
        {
            await me.RequireAsync(Permissions.PayoutApprove);
            if (string.IsNullOrWhiteSpace(req.Reference)) throw AppException.Invalid("Enter the bank or provider reference.");
            var p = await commissions.MarkPaidAsync(payoutId, me.Id, req.Reference.Trim());
            return new { p.Id, p.Status };
        });
    }

    private static System.Linq.Expressions.Expression<Func<CommissionEntry, object>> Project(TmsDbContext db) => e => new
    {
        e.Id, e.OrderId, e.RecipientKind, e.EntryType, e.BaseMinor, e.RateBp, e.AmountMinor, e.Currency,
        e.PlanVersion, e.Status, e.ReleaseStage, e.ReleasedAt, e.AvailableAt, e.CreatedAt, e.Calculation, e.PayoutId,
        Course = db.OrderItems.Where(i => i.Id == e.OrderItemId).Select(i => i.CourseTitle).FirstOrDefault(),
        Recipient = e.RecipientTenantId != null
            ? db.Tenants.Where(t => t.Id == e.RecipientTenantId).Select(t => t.Name).FirstOrDefault()
            : db.Users.Where(u => u.Id == e.RecipientUserId).Select(u => u.FullName).FirstOrDefault(),
        Plan = db.CommissionPlans.Where(p => p.Id == e.PlanId).Select(p => p.Name).FirstOrDefault(),
    };
}
