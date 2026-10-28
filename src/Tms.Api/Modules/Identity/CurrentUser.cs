using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tms.Api.Common;
using Tms.Api.Data;

namespace Tms.Api.Modules.Identity;

public record MembershipInfo(Guid TenantId, string TenantName, TenantKind TenantKind, string Role);

/// <summary>
/// Per-request view of the caller. Authorization is action-based and tenant-scoped:
/// a permission is granted in a tenant when the user holds a role there that carries it,
/// or globally when they hold that role in the platform tenant.
/// </summary>
public class CurrentUser(IHttpContextAccessor http, TmsDbContext db)
{
    private List<MembershipInfo>? _memberships;

    public Guid? IdOrNull
    {
        get
        {
            var sub = http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    public Guid Id => IdOrNull ?? throw new AppException(StatusCodes.Status401Unauthorized, "Sign in required.");

    public async Task<IReadOnlyList<MembershipInfo>> MembershipsAsync()
    {
        if (_memberships is not null) return _memberships;
        if (IdOrNull is not { } userId) return _memberships = [];
        _memberships = await db.TenantMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new MembershipInfo(m.TenantId, m.Tenant!.Name, m.Tenant.Kind, m.Role))
            .ToListAsync();
        return _memberships;
    }

    public async Task<bool> HasPermissionAsync(string permission, Guid? tenantId = null)
    {
        foreach (var m in await MembershipsAsync())
        {
            if (!Permissions.For(m.Role).Contains(permission)) continue;
            if (m.TenantKind == TenantKind.Platform && Permissions.IsPlatformRole(m.Role)) return true;
            if (tenantId is not null && m.TenantId == tenantId) return true;
        }
        return false;
    }

    public async Task RequireAsync(string permission, Guid? tenantId = null)
    {
        if (!await HasPermissionAsync(permission, tenantId)) throw AppException.Forbidden();
    }

    public async Task<bool> IsMemberOfAsync(Guid tenantId) =>
        (await MembershipsAsync()).Any(m => m.TenantId == tenantId);

    public async Task<IEnumerable<string>> PermissionsInAsync(Guid tenantId) =>
        (await MembershipsAsync())
            .Where(m => m.TenantId == tenantId || (m.TenantKind == TenantKind.Platform && Permissions.IsPlatformRole(m.Role)))
            .SelectMany(m => Permissions.For(m.Role))
            .Distinct();
}
