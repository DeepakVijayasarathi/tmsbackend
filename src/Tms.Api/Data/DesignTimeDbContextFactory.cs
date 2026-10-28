using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tms.Api.Data;

/// <summary>Used by `dotnet ef`; migrations target PostgreSQL, the production database.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TmsDbContext>
{
    public TmsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TmsDbContext>()
            .UseNpgsql("Host=localhost;Database=tms;Username=tms;Password=tms")
            .Options);
}
