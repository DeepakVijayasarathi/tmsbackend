using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Tms.Api.Common;
using Tms.Api.Data;

namespace Tms.Api.Modules.Identity;

public record RegisterRequest(string Email, string Password, string FullName, string AccountType, string? OrganizationName);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string Token, DateTime ExpiresAt);
public record AddMemberRequest(string Email, string Role);
public record UpdateProfileRequest(string? FullName, string? Headline);

public class TokenService(IConfiguration config)
{
    public static SymmetricSecurityKey SigningKey(IConfiguration config)
    {
        var key = config["Auth:JwtKey"];
        if (string.IsNullOrEmpty(key) || key.Length < 32)
            throw new InvalidOperationException("Auth:JwtKey must be configured with at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    public AuthResponse Issue(User user)
    {
        var expires = DateTime.UtcNow.AddHours(config.GetValue("Auth:TokenLifetimeHours", 8));
        var token = new JwtSecurityToken(
            issuer: config["Auth:Issuer"],
            audience: config["Auth:Issuer"],
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.FullName),
            ],
            expires: expires,
            signingCredentials: new SigningCredentials(SigningKey(config), SecurityAlgorithms.HmacSha256));
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public static class IdentityEndpoints
{
    private static readonly PasswordHasher<User> Hasher = new();

    public static string HashPassword(User user, string password) => Hasher.HashPassword(user, password);

    public static void MapIdentity(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Identity").RequireRateLimiting("auth");

        auth.MapPost("/register", async (RegisterRequest req, TmsDbContext db, TokenService tokens) =>
        {
            var email = req.Email?.Trim() ?? "";
            if (!email.Contains('@') || email.Length > 254) throw AppException.Invalid("Enter a valid email address.");
            if (string.IsNullOrWhiteSpace(req.FullName)) throw AppException.Invalid("Enter your name.");
            if ((req.Password?.Length ?? 0) < 8) throw AppException.Invalid("Password must be at least 8 characters.");
            var normalized = email.ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized))
                throw AppException.Conflict("An account with this email already exists.");

            var user = new User { Email = email, NormalizedEmail = normalized, FullName = req.FullName.Trim() };
            user.PasswordHash = Hasher.HashPassword(user, req.Password!);
            db.Users.Add(user);

            switch (req.AccountType?.ToLowerInvariant())
            {
                case "learner" or null or "":
                    break;
                case "professor":
                {
                    var tenant = await NewTenantAsync(db, req.OrganizationName is { Length: > 0 } n ? n : $"{user.FullName} Academy", TenantKind.TrainingProvider);
                    db.TenantMemberships.Add(new TenantMembership { TenantId = tenant.Id, UserId = user.Id, Role = Roles.ProviderAdmin });
                    db.TenantMemberships.Add(new TenantMembership { TenantId = tenant.Id, UserId = user.Id, Role = Roles.Professor });
                    break;
                }
                case "company":
                {
                    // Vendor partner: a company that publishes its own courses and issues certificates under its name.
                    if (string.IsNullOrWhiteSpace(req.OrganizationName)) throw AppException.Invalid("Enter the company name.");
                    var tenant = await NewTenantAsync(db, req.OrganizationName, TenantKind.TrainingProvider);
                    db.TenantMemberships.Add(new TenantMembership { TenantId = tenant.Id, UserId = user.Id, Role = Roles.ProviderAdmin });
                    db.TenantMemberships.Add(new TenantMembership { TenantId = tenant.Id, UserId = user.Id, Role = Roles.Professor });
                    break;
                }
                case "college":
                {
                    if (string.IsNullOrWhiteSpace(req.OrganizationName)) throw AppException.Invalid("Enter the college name.");
                    var tenant = await NewTenantAsync(db, req.OrganizationName, TenantKind.College);
                    db.TenantMemberships.Add(new TenantMembership { TenantId = tenant.Id, UserId = user.Id, Role = Roles.CollegeAdmin });
                    break;
                }
                default:
                    throw AppException.Invalid("Account type must be learner, professor, company or college.");
            }

            Audit.Record(db, user.Id, "user.registered", nameof(User), user.Id, new { req.AccountType });
            await db.SaveChangesAsync();
            return tokens.Issue(user);
        });

        auth.MapPost("/login", async (LoginRequest req, TmsDbContext db, TokenService tokens) =>
        {
            var normalized = (req.Email ?? "").Trim().ToUpperInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized);
            var result = user is null ? PasswordVerificationResult.Failed : Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "");
            if (result == PasswordVerificationResult.Failed)
                throw new AppException(StatusCodes.Status401Unauthorized, "Email or password is incorrect.");
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user!.PasswordHash = Hasher.HashPassword(user, req.Password!);
                await db.SaveChangesAsync();
            }
            return tokens.Issue(user!);
        });

        var me = api.MapGroup("/me").WithTags("Identity").RequireAuthorization();

        me.MapGet("", async (CurrentUser current, TmsDbContext db) =>
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == current.Id);
            var memberships = await current.MembershipsAsync();
            var tenants = new List<object>();
            foreach (var g in memberships.GroupBy(m => m.TenantId))
                tenants.Add(new
                {
                    id = g.Key,
                    name = g.First().TenantName,
                    kind = g.First().TenantKind,
                    roles = g.Select(m => m.Role),
                    permissions = await current.PermissionsInAsync(g.Key),
                });
            return new
            {
                user.Id, user.Email, user.FullName, user.Headline,
                interests = Careers.RecommendationService.SplitInterests(user.Interests),
                isPlatformStaff = memberships.Any(m => m.TenantKind == TenantKind.Platform && Permissions.IsPlatformRole(m.Role)),
                tenants,
            };
        });

        me.MapPatch("", async (UpdateProfileRequest req, CurrentUser current, TmsDbContext db) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == current.Id);
            if (!string.IsNullOrWhiteSpace(req.FullName)) user.FullName = req.FullName.Trim();
            if (req.Headline is not null) user.Headline = req.Headline.Trim();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var tenants = api.MapGroup("/tenants").WithTags("Tenancy").RequireAuthorization();

        tenants.MapGet("/{tenantId:guid}/members", async (Guid tenantId, CurrentUser current, TmsDbContext db) =>
        {
            await current.RequireAsync(Permissions.MembersManage, tenantId);
            return await db.TenantMemberships.AsNoTracking().Where(m => m.TenantId == tenantId)
                .GroupBy(m => new { m.UserId, m.User!.FullName, m.User.Email })
                .Select(g => new { g.Key.UserId, g.Key.FullName, g.Key.Email, Roles = g.Select(m => m.Role).ToList() })
                .ToListAsync();
        });

        tenants.MapPost("/{tenantId:guid}/members", async (Guid tenantId, AddMemberRequest req, CurrentUser current, TmsDbContext db) =>
        {
            await current.RequireAsync(Permissions.MembersManage, tenantId);
            var tenant = await db.Tenants.FindAsync(tenantId) ?? throw AppException.NotFound("Organization");
            if (!Roles.All.Contains(req.Role) || Permissions.IsPlatformRole(req.Role) != (tenant.Kind == TenantKind.Platform))
                throw AppException.Invalid("That role cannot be granted in this organization.");
            var normalized = req.Email.Trim().ToUpperInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized)
                ?? throw new AppException(StatusCodes.Status404NotFound, "No user with that email has registered yet.");
            if (await db.TenantMemberships.AnyAsync(m => m.TenantId == tenantId && m.UserId == user.Id && m.Role == req.Role))
                return Results.NoContent();
            db.TenantMemberships.Add(new TenantMembership { TenantId = tenantId, UserId = user.Id, Role = req.Role });
            Audit.Record(db, current.Id, "member.added", nameof(TenantMembership), user.Id, new { req.Role }, tenantId);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task<Tenant> NewTenantAsync(TmsDbContext db, string name, TenantKind kind)
    {
        var slug = Text.Slugify(name);
        if (await db.Tenants.AnyAsync(t => t.Slug == slug)) slug = $"{slug}-{Text.RandomCode(4).ToLower()}";
        var tenant = new Tenant { Name = name.Trim(), Slug = slug, Kind = kind };
        db.Tenants.Add(tenant);
        return tenant;
    }
}
