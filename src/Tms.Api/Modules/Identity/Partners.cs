using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Catalog;
using Tms.Api.Modules.Certificates;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Identity;

public record PartnerVerifyRequest(bool Verified);

/// <summary>
/// Vendor partners: companies that join the platform to publish their own courses and issue
/// certificates under their name, co-branded with the platform. A partner is a TrainingProvider or
/// Corporate tenant; platform staff verify it before it is listed publicly and badged as a partner.
/// </summary>
public static class PartnerEndpoints
{
    public static bool IsPartnerKind(TenantKind kind) => kind is TenantKind.TrainingProvider or TenantKind.Corporate;

    public static void MapPartners(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/partners").WithTags("Identity");

        // Public: verified partners with at least one public course. Platform staff see every partner org, with ?all=true.
        g.MapGet("", async (bool? all, CurrentUser me, TmsDbContext db) =>
        {
            var staff = all == true && await me.HasPermissionAsync(Permissions.MembersManage) && (await me.MembershipsAsync()).Any(m => m.TenantKind == TenantKind.Platform);
            var published = db.Courses.Where(c => c.Status == CourseStatus.Published && c.Visibility == CourseVisibility.Public);
            var query = db.Tenants.AsNoTracking().Where(t => t.Kind == TenantKind.TrainingProvider || t.Kind == TenantKind.Corporate);
            if (!staff) query = query.Where(t => t.IsVerified && published.Any(c => c.TenantId == t.Id));

            return await query.OrderByDescending(t => t.IsVerified).ThenBy(t => t.Name).Select(t => new
            {
                t.Id, t.Name, t.Slug, t.Kind, t.BrandColor, t.IsVerified, t.CreatedAt,
                Courses = published.Where(c => c.TenantId == t.Id).OrderByDescending(c => c.UpdatedAt)
                    .Select(c => new { c.Id, c.Slug, c.Title, c.Summary, c.Category, c.PriceMinor, c.Currency }).Take(6).ToList(),
                Learners = db.Enrollments.Count(e => e.Course!.TenantId == t.Id && e.Status != EnrollmentStatus.Revoked),
                CertificatesIssued = db.Certificates.Count(c => c.IssuerTenantId == t.Id && c.Status == CertificateStatus.Active),
            }).ToListAsync();
        });

        g.MapPatch("/{tenantId:guid}/verify", async (Guid tenantId, PartnerVerifyRequest req, CurrentUser me, TmsDbContext db) =>
        {
            if (!(await me.MembershipsAsync()).Any(m => m.TenantKind == TenantKind.Platform && m.Role == Roles.SuperAdmin))
                throw AppException.Forbidden("Only platform admins can verify partners.");
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId) ?? throw AppException.NotFound("Partner");
            if (!IsPartnerKind(tenant.Kind)) throw AppException.Invalid("Only company and training-provider organizations can be partners.");
            tenant.IsVerified = req.Verified;
            Audit.Record(db, me.Id, req.Verified ? "partner.verified" : "partner.unverified", nameof(Tenant), tenant.Id, null, tenant.Id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
