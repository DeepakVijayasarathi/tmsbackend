using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tms.Api.Common;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Thrown for expected business failures; mapped to RFC 7807 problem responses.</summary>
public class AppException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public static AppException NotFound(string what) => new(StatusCodes.Status404NotFound, $"{what} not found.");
    public static AppException Forbidden(string? why = null) => new(StatusCodes.Status403Forbidden, why ?? "You do not have permission to do that.");
    public static AppException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static AppException Invalid(string message) => new(StatusCodes.Status400BadRequest, message);
}

public static partial class Text
{
    public static string Slugify(string value)
    {
        var slug = NonSlugChars().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 80 ? slug[..80].TrimEnd('-') : slug;
    }

    /// <summary>Human-friendly random code without ambiguous characters (0/O, 1/I).</summary>
    public static string RandomCode(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    public static string HmacSha256Hex(string secret, string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool FixedTimeEqualsHex(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a.ToLowerInvariant()), Encoding.ASCII.GetBytes(b.ToLowerInvariant()));

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}
