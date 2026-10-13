using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using SupportDesk.Domain.Repositories;

namespace SupportDesk.Presentation.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddAgentAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
        if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32 || string.IsNullOrWhiteSpace(jwt.Issuer)
            || string.IsNullOrWhiteSpace(jwt.Audience) || jwt.LifetimeMinutes <= 0)
            throw new InvalidOperationException("Configure Jwt:SigningKey (at least 32 bytes), issuer, audience and a positive lifetime.");
        services.AddSingleton(jwt);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                NameClaimType = "name",
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    if (!int.TryParse(context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id))
                    {
                        context.Fail("Invalid agent identity.");
                        return;
                    }
                    var agents = context.HttpContext.RequestServices.GetRequiredService<IAgentRepository>();
                    var agent = await agents.GetByIdAsync(id, context.HttpContext.RequestAborted);
                    if (agent is null || !agent.IsActive) context.Fail("Agent is inactive or missing.");
                }
            };
        });
        // One central rule protects every endpoint, including new ones added later.
        services.AddAuthorization(options => options.FallbackPolicy =
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}

public sealed class JwtSettings
{
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "NorthwindSupportDesk";
    public string Audience { get; set; } = "NorthwindSupportDeskWeb";
    public int LifetimeMinutes { get; set; } = 60;
}
