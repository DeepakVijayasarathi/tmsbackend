using Microsoft.EntityFrameworkCore;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Commerce;
using Tms.Api.Modules.Commissions;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Analytics;

public static class AnalyticsEndpoints
{
    private static readonly CommissionStatus[] Outstanding = [CommissionStatus.Pending, CommissionStatus.Approved, CommissionStatus.Payable, CommissionStatus.OnHold];

    public static void MapAnalytics(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/analytics").WithTags("Analytics").RequireAuthorization();

        g.MapGet("/teaching", async (CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            var courses = await db.Courses.AsNoTracking().Where(c => c.InstructorId == userId).Select(c => new
            {
                c.Id, c.Title, c.Slug, c.Status, c.PriceMinor,
                Enrollments = db.Enrollments.Count(e => e.CourseId == c.Id && e.Status != EnrollmentStatus.Revoked),
                Completions = db.Enrollments.Count(e => e.CourseId == c.Id && e.Status == EnrollmentStatus.Completed),
                AvgScore = db.AssessmentAttempts.Where(t => db.Enrollments.Any(e => e.Id == t.EnrollmentId && e.CourseId == c.Id) && t.SubmittedAt != null)
                    .Average(t => (double?)t.ScorePercent),
                GrossSalesMinor = db.OrderItems.Where(i => i.CourseId == c.Id && db.Orders.Any(o => o.Id == i.OrderId && o.PaidAt != null))
                    .Sum(i => (long?)i.UnitPriceMinor) ?? 0,
            }).ToListAsync();
            return new { courses };
        });

        g.MapGet("/tenants/{tenantId:guid}", async (Guid tenantId, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.AnalyticsView, tenantId);
            return new
            {
                members = await db.TenantMemberships.Where(m => m.TenantId == tenantId).Select(m => m.UserId).Distinct().CountAsync(),
                faculty = await db.TenantMemberships.CountAsync(m => m.TenantId == tenantId && m.Role == Roles.Professor),
                courses = await db.Courses.CountAsync(c => c.TenantId == tenantId),
                published = await db.Courses.CountAsync(c => c.TenantId == tenantId && c.Status == CourseStatus.Published),
                enrollments = await db.Enrollments.CountAsync(e => e.Course!.TenantId == tenantId && e.Status != EnrollmentStatus.Revoked),
                completions = await db.Enrollments.CountAsync(e => e.Course!.TenantId == tenantId && e.Status == EnrollmentStatus.Completed),
                certificates = await db.Certificates.CountAsync(c => c.IssuerTenantId == tenantId && c.Status == CertificateStatus.Active),
                courseBreakdown = await db.Courses.Where(c => c.TenantId == tenantId).Select(c => new
                {
                    c.Title, c.Status, Instructor = c.Instructor!.FullName,
                    Enrollments = db.Enrollments.Count(e => e.CourseId == c.Id && e.Status != EnrollmentStatus.Revoked),
                    Completions = db.Enrollments.Count(e => e.CourseId == c.Id && e.Status == EnrollmentStatus.Completed),
                }).ToListAsync(),
            };
        });

        // Separates collected revenue, refunds, tax and outstanding commission liabilities.
        g.MapGet("/platform", async (CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.AnalyticsView);
            var paid = db.Orders.Where(o => o.PaidAt != null);
            var liabilities = db.CommissionEntries.Where(e => e.RecipientKind != RecipientKind.Platform && Outstanding.Contains(e.Status));
            return new
            {
                tenants = await db.Tenants.CountAsync(t => t.Kind != TenantKind.Platform),
                users = await db.Users.CountAsync(),
                publishedCourses = await db.Courses.CountAsync(c => c.Status == CourseStatus.Published),
                activeEnrollments = await db.Enrollments.CountAsync(e => e.Status == EnrollmentStatus.Active),
                certificatesIssued = await db.Certificates.CountAsync(),
                paidOrders = await paid.CountAsync(),
                pendingOrders = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending),
                collectedMinor = await paid.SumAsync(o => (long?)o.TotalMinor) ?? 0,
                refundedMinor = await paid.SumAsync(o => (long?)o.RefundedMinor) ?? 0,
                taxMinor = await paid.Where(o => o.Status != OrderStatus.Refunded).SumAsync(o => (long?)o.TaxMinor) ?? 0,
                platformShareMinor = await db.CommissionEntries.Where(e => e.RecipientKind == RecipientKind.Platform).SumAsync(e => (long?)e.AmountMinor) ?? 0,
                commissionLiabilityMinor = await liabilities.SumAsync(e => (long?)e.AmountMinor) ?? 0,
                liabilityByStatus = await liabilities.GroupBy(e => e.Status).Select(x => new { status = x.Key, amountMinor = x.Sum(e => e.AmountMinor) }).ToListAsync(),
                paidOutMinor = await db.CommissionEntries.Where(e => e.Status == CommissionStatus.Paid).SumAsync(e => (long?)e.AmountMinor) ?? 0,
                aiMessages = await db.AiMessages.CountAsync(m => m.Role == "assistant"),
                aiTokens = await db.AiMessages.SumAsync(m => (long?)(m.InputTokens + m.OutputTokens)) ?? 0,
            };
        });
    }
}
