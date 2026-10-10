using System.Text;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ConnectedOps.Api;

internal static class AuthExtensions
{
    public static WebApplicationBuilder AddConnectedOperationsAuth(
        this WebApplicationBuilder builder)
    {
        var jwtSection = builder.Configuration.GetSection("Authentication:Jwt");
        var issuer = jwtSection["Issuer"]?.Trim();
        var audience = jwtSection["Audience"]?.Trim();
        var signingKey = jwtSection["SigningKey"]?.Trim();
        var lifetimeMinutes = jwtSection.GetValue("LifetimeMinutes", 480);

        if (string.IsNullOrWhiteSpace(issuer)
            || string.IsNullOrWhiteSpace(audience)
            || string.IsNullOrWhiteSpace(signingKey)
            || signingKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Authentication:Jwt Issuer, Audience, and a SigningKey of at least 32 characters are required.");
        }

        if (lifetimeMinutes is < 5 or > 24 * 60)
        {
            throw new InvalidOperationException(
                "Authentication:Jwt LifetimeMinutes must be between 5 and 1440.");
        }

        var options = new JwtAuthOptions(issuer, audience, signingKey, lifetimeMinutes);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IDemoUserDirectory, DemoUserDirectory>();
        builder.Services.AddSingleton<IAuthTokenService, JwtTokenService>();

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(options.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role,
                    NameClaimType = System.Security.Claims.ClaimTypes.Name
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(
                AuthPolicies.CanOperate,
                policy => policy.RequireRole(AuthRoles.Operator))
            .AddPolicy(
                AuthPolicies.CanApprove,
                policy => policy.RequireRole(AuthRoles.Approver));

        return builder;
    }
}
