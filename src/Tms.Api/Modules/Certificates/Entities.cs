using Tms.Api.Common;

namespace Tms.Api.Modules.Certificates;

public enum CertificateStatus { Active, Revoked }
public enum CertificateEventType { Issued, Revoked, Shared, Verified }

public class Certificate : Entity
{
    public required string CredentialCode { get; set; }
    public Guid UserId { get; set; }
    public Guid CourseId { get; set; }
    public Guid CourseVersionId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid IssuerTenantId { get; set; }

    // Snapshot at issue time so later renames never alter an issued credential.
    public required string RecipientName { get; set; }
    public required string CourseTitle { get; set; }
    public required string IssuerName { get; set; }
    public int? ScorePercent { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public CertificateStatus Status { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}

public class CertificateEvent : Entity
{
    public Guid CertificateId { get; set; }
    public CertificateEventType Type { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Detail { get; set; }
}
