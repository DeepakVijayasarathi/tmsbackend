using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Tms.Api.Common;

namespace Tms.Api.Modules.Classroom;

/// <summary>
/// Signs Jitsi join tokens in the "iss/aud/sub/room + context.user" shape prosody's mod_auth_token
/// expects — the same shape the Meet to Manage deployment verifies. Returns null until
/// Jitsi:AppId and Jitsi:AppSecret are configured, so an open (unauthenticated) Jitsi domain keeps working.
/// </summary>
public class JitsiTokenService(IConfiguration config)
{
    public string Domain => config["Jitsi:Domain"] is { Length: > 0 } d ? d : "meet.techmisai.com";
    public int JoinEarlyMinutes => config.GetValue("Jitsi:JoinEarlyMinutes", 15);

    public string? CreateToken(string room, Guid userId, string name, string email, bool moderator, DateTime expiresAt)
    {
        var appId = config["Jitsi:AppId"];
        var appSecret = config["Jitsi:AppSecret"];
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret)) return null;

        var now = DateTime.UtcNow;
        var payload = new JwtPayload(issuer: appId, audience: appId, claims: null, notBefore: now.AddMinutes(-1), expires: expiresAt, issuedAt: now)
        {
            ["sub"] = Domain,
            ["room"] = room,
            ["context"] = new Dictionary<string, object?>
            {
                ["user"] = new Dictionary<string, object?>
                {
                    ["id"] = userId.ToString(),
                    ["name"] = name,
                    ["email"] = email,
                    ["moderator"] = moderator,
                },
            },
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(appSecret)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(credentials), payload));
    }

    /// <summary>Lower-case, URL-safe room name with an unguessable suffix.</summary>
    public static string NewRoomName(string title)
    {
        var slug = Text.Slugify(title);
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return $"tms-{slug}-{Text.RandomCode(10).ToLowerInvariant()}";
    }
}
