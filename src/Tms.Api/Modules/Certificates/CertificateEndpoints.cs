using Microsoft.EntityFrameworkCore;
using QRCoder;
using Tms.Api.Common;
using Tms.Api.Data;
using Tms.Api.Modules.Identity;
using Tms.Api.Modules.Learning;

namespace Tms.Api.Modules.Certificates;

public record RevokeRequest(string Reason);

public static class CertificateEndpoints
{
    public static void MapCertificates(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Certificates");

        g.MapGet("/me/certificates", async (CurrentUser me, TmsDbContext db) =>
        {
            var userId = me.Id;
            return await db.Certificates.AsNoTracking().Where(c => c.UserId == userId).OrderByDescending(c => c.IssuedAt)
                .Select(c => new { c.Id, c.CredentialCode, c.CourseTitle, c.IssuerName, c.IssuedAt, c.ExpiresAt, c.Status, c.ScorePercent })
                .ToListAsync();
        }).RequireAuthorization();

        g.MapPost("/enrollments/{enrollmentId:guid}/certificate", async (Guid enrollmentId, CurrentUser me,
            EnrollmentService enrollments, CertificateService certificates) =>
        {
            var e = await enrollments.GetOwnActiveAsync(enrollmentId, me.Id);
            var cert = await certificates.TryIssueAsync(e);
            return cert is null
                ? Results.UnprocessableEntity(new { title = "Not yet eligible", eligibility = await certificates.EvaluateAsync(e) })
                : Results.Ok(new { cert.Id, cert.CredentialCode });
        }).RequireAuthorization();

        // Public verification discloses only what a verifier needs.
        g.MapGet("/certificates/{code}/verify", async (string code, TmsDbContext db) =>
        {
            var c = await db.Certificates.AsNoTracking().FirstOrDefaultAsync(x => x.CredentialCode == code.ToUpper())
                ?? throw AppException.NotFound("Credential");
            var state = c.Status == CertificateStatus.Revoked ? "revoked"
                : c.ExpiresAt is { } exp && exp < DateTime.UtcNow ? "expired"
                : "valid";
            return new
            {
                c.CredentialCode, c.RecipientName, c.CourseTitle, c.IssuerName, c.IssuedAt, c.ExpiresAt, c.ScorePercent,
                State = state,
                // Partner-issued credentials are co-branded with the platform.
                IssuerPartner = await db.Tenants.AnyAsync(t => t.Id == c.IssuerTenantId && t.IsVerified && (t.Kind == TenantKind.TrainingProvider || t.Kind == TenantKind.Corporate)),
                RevokedAt = state == "revoked" ? c.RevokedAt : null,
            };
        });

        g.MapGet("/certificates/{code}/qr.png", async (string code, TmsDbContext db, IConfiguration config) =>
        {
            if (!await db.Certificates.AnyAsync(x => x.CredentialCode == code.ToUpper())) throw AppException.NotFound("Credential");
            var url = $"{config["App:PublicWebUrl"]?.TrimEnd('/')}/verify/{code.ToUpper()}";
            using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
            return Results.File(new PngByteQRCode(data).GetGraphic(8), "image/png");
        });

        g.MapPost("/certificates/{certificateId:guid}/revoke", async (Guid certificateId, RevokeRequest req, CurrentUser me, TmsDbContext db) =>
        {
            var c = await db.Certificates.FindAsync(certificateId) ?? throw AppException.NotFound("Certificate");
            await me.RequireAsync(Permissions.CertificateRevoke, c.IssuerTenantId);
            if (string.IsNullOrWhiteSpace(req.Reason)) throw AppException.Invalid("A reason is required to revoke a credential.");
            if (c.Status == CertificateStatus.Revoked) return Results.NoContent();
            c.Status = CertificateStatus.Revoked;
            c.RevokedAt = DateTime.UtcNow;
            c.RevokedReason = req.Reason.Trim();
            db.CertificateEvents.Add(new CertificateEvent { CertificateId = c.Id, Type = CertificateEventType.Revoked, ActorUserId = me.Id, Detail = c.RevokedReason });
            Audit.Record(db, me.Id, "certificate.revoked", nameof(Certificate), c.Id, new { req.Reason }, c.IssuerTenantId);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/tenants/{tenantId:guid}/certificates", async (Guid tenantId, CurrentUser me, TmsDbContext db) =>
        {
            await me.RequireAsync(Permissions.CertificateRevoke, tenantId);
            return await db.Certificates.AsNoTracking().Where(c => c.IssuerTenantId == tenantId).OrderByDescending(c => c.IssuedAt).Take(200)
                .Select(c => new { c.Id, c.CredentialCode, c.RecipientName, c.CourseTitle, c.IssuedAt, c.Status, c.RevokedReason })
                .ToListAsync();
        }).RequireAuthorization();

        // LinkedIn: user-initiated "Add to profile" and share links. No profile writes happen server-side.
        g.MapPost("/certificates/{certificateId:guid}/share", async (Guid certificateId, CurrentUser me, TmsDbContext db, IConfiguration config) =>
        {
            var c = await db.Certificates.FirstOrDefaultAsync(x => x.Id == certificateId && x.UserId == me.Id) ?? throw AppException.NotFound("Certificate");
            if (c.Status != CertificateStatus.Active) throw AppException.Conflict("Revoked credentials cannot be shared.");
            var verifyUrl = $"{config["App:PublicWebUrl"]?.TrimEnd('/')}/verify/{c.CredentialCode}";
            var addToProfile = "https://www.linkedin.com/profile/add?startTask=CERTIFICATION_NAME"
                + $"&name={Uri.EscapeDataString(c.CourseTitle)}"
                + $"&organizationName={Uri.EscapeDataString(c.IssuerName)}"
                + $"&issueYear={c.IssuedAt.Year}&issueMonth={c.IssuedAt.Month}"
                + (c.ExpiresAt is { } e ? $"&expirationYear={e.Year}&expirationMonth={e.Month}" : "")
                + $"&certUrl={Uri.EscapeDataString(verifyUrl)}&certId={Uri.EscapeDataString(c.CredentialCode)}";
            var share = $"https://www.linkedin.com/sharing/share-offsite/?url={Uri.EscapeDataString(verifyUrl)}";
            db.CertificateEvents.Add(new CertificateEvent { CertificateId = c.Id, Type = CertificateEventType.Shared, ActorUserId = me.Id, Detail = "linkedin" });
            await db.SaveChangesAsync();
            return new { verifyUrl, addToProfileUrl = addToProfile, shareUrl = share };
        }).RequireAuthorization();
    }
}
