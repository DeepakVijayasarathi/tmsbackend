using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Identity;

namespace Tms.Api.Modules.Referrals;

public record CreateCodeRequest(string? Campaign);

public static class ReferralEndpoints
{
    public static void MapReferrals(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/referrals").WithTags("Referrals").RequireAuthorization();

        g.MapPost("/codes", async (CreateCodeRequest req, CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var campaign = string.IsNullOrWhiteSpace(req.Campaign) ? null : req.Campaign.Trim();
            var existing = await db.ReferralCodes.FirstOrDefaultAsync(r => r.OwnerUserId == userId && r.Campaign == campaign && r.IsActive);
            if (existing is not null) return new { existing.Id, existing.Code };

            string code;
            do code = Text.RandomCode(8); while (await db.ReferralCodes.AnyAsync(r => r.Code == code));
            var entity = new ReferralCode { Code = code, OwnerUserId = userId, Campaign = campaign };
            db.ReferralCodes.Add(entity);
            await db.SaveChangesAsync();
            return new { entity.Id, entity.Code };
        });

        g.MapGet("/dashboard", async (CurrentUser me, TmsDbContext db, CommissionService commissions, IConfiguration config) =>
        {
            var userId = me.Id;
            var codes = await db.ReferralCodes.AsNoTracking().Where(r => r.OwnerUserId == userId)
                .Select(r => new
                {
                    r.Id, r.Code, r.Campaign, r.IsActive, r.CreatedAt,
                    Conversions = db.ReferralConversions.Count(c => c.ReferralCodeId == r.Id && c.Status == ReferralConversionStatus.Credited),
                }).ToListAsync();
            var conversions = await db.ReferralConversions.AsNoTracking().Where(c => c.ReferrerUserId == userId)
                .OrderByDescending(c => c.CreatedAt).Take(50)
                .Select(c => new
                {
                    c.Id, c.Status, c.Note, c.CreatedAt,
                    Course = db.OrderItems.Where(i => i.OrderId == c.OrderId).Select(i => i.CourseTitle).FirstOrDefault(),
                    Earned = db.CommissionEntries.Where(e => e.OrderId == c.OrderId && e.RecipientKind == RecipientKind.Referrer && e.RecipientUserId == userId)
                        .Sum(e => (long?)e.AmountMinor) ?? 0,
                }).ToListAsync();
            var balance = await commissions.BalanceAsync(
                commissions.ForRecipient(new Recipient(RecipientKind.Referrer, userId, null)), config["Commerce:DefaultCurrency"] ?? "INR");
            return new { codes, conversions, balance };
        });
    }
}
