using Tms.Api.Common;

namespace Tms.Api.Modules.Identity;

public enum TenantKind { Platform, College, TrainingProvider, Corporate }

public class Tenant : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public TenantKind Kind { get; set; }
    public string? BrandColor { get; set; }
    public bool IsVerified { get; set; }
}

public class User : Entity
{
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string FullName { get; set; }
    public string PasswordHash { get; set; } = "";
    public string? Headline { get; set; }
    /// <summary>Learner interests, "|"-separated; drives recommendations.</summary>
    public string Interests { get; set; } = "";
}

/// <summary>A user's role inside one tenant. A user may hold several roles across several tenants.</summary>
public class TenantMembership : Entity
{
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public required string Role { get; set; }
}

public class AuditLog : Entity
{
    public Guid? ActorUserId { get; set; }
    public Guid? TenantId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Data { get; set; }
}
