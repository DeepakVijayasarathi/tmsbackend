using System.Text.Json;
using Tms.Api.Data;

namespace Tms.Api.Modules.Identity;

public static class Audit
{
    /// <summary>Adds an audit row to the current unit of work; it commits with the change it describes.</summary>
    public static void Record(TmsDbContext db, Guid? actor, string action, string entityType, Guid? entityId,
        object? data = null, Guid? tenantId = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor,
            TenantId = tenantId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Data = data is null ? null : JsonSerializer.Serialize(data),
        });
}
