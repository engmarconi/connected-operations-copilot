using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ConnectedOps.Application;
using Microsoft.IdentityModel.Tokens;

namespace ConnectedOps.Api;

public sealed record JwtAuthOptions(
    string Issuer,
    string Audience,
    string SigningKey,
    int LifetimeMinutes);

public sealed class JwtTokenService(
    JwtAuthOptions options,
    TimeProvider timeProvider) : IAuthTokenService
{
    public AuthTokenResult IssueToken(AuthUser user)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(options.LifetimeMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Username),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(ClaimTypes.Name, user.Username),
            new("name", user.DisplayName)
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new AuthTokenResult(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt,
            user);
    }
}
