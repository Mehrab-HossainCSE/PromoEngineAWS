using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PromoEngine.Domain.Catalog;

namespace PromoEngine.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "PromoEngine";
    public string Audience { get; set; } = "PromoEngine.Client";

    /// <summary>Signing key. Override this in every deployed environment.</summary>
    public string SigningKey { get; set; } = "development-only-signing-key-change-me-please-32chars";

    public int AccessTokenMinutes { get; set; } = 480;
    public int GuestTokenMinutes { get; set; } = 120;
}

/// <summary>Claim names shared by the API and the tenant resolution middleware.</summary>
public static class PromoClaims
{
    public const string TenantId = "tenant_id";
    public const string TenantSlug = "tenant_slug";
    public const string IsGuest = "is_guest";
    public const string GuestSessionId = "guest_session";
}

public interface IJwtTokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateUserToken(TenantUser user, Tenant tenant);
    (string Token, DateTimeOffset ExpiresAt) CreateGuestToken(Guid guestSessionId);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) CreateUserToken(TenantUser user, Tenant tenant)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(PromoClaims.TenantId, tenant.Id.ToString()),
            new(PromoClaims.TenantSlug, tenant.Slug),
            new(PromoClaims.IsGuest, "false")
        };

        return Create(claims, TimeSpan.FromMinutes(_options.AccessTokenMinutes));
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateGuestToken(Guid guestSessionId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, guestSessionId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Guest"),
            new(ClaimTypes.Role, "Guest"),
            new(PromoClaims.IsGuest, "true"),
            new(PromoClaims.GuestSessionId, guestSessionId.ToString())
        };

        return Create(claims, TimeSpan.FromMinutes(_options.GuestTokenMinutes));
    }

    private (string Token, DateTimeOffset ExpiresAt) Create(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTimeOffset.UtcNow.Add(lifetime);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
