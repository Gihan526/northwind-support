using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SupportDesk.Application.Abstractions;
using SupportDesk.Application.Contracts.Auth;
using SupportDesk.Domain.Repositories;
using SupportDesk.Presentation.Extensions;

namespace SupportDesk.Presentation.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAgentRepository agents, IPasswordService passwords, IClock clock, JwtSettings jwt) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var agent = await agents.GetByEmailAsync(request.Email, ct);
        if (agent is null || !agent.IsActive || agent.PasswordHash is null
            || !passwords.Verify(agent.Email, agent.PasswordHash, request.Password))
        {
            Response.Headers.WWWAuthenticate = "Bearer";
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Unauthorized", Detail = "Email or password is incorrect." });
        }

        var now = clock.UtcNow;
        var expires = now.AddMinutes(jwt.LifetimeMinutes);
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, agent.Id.ToString(CultureInfo.InvariantCulture)),
             new Claim("name", agent.FullName), new Claim(JwtRegisteredClaimNames.Email, agent.Email),
             new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
             new Claim(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
            notBefore: now, expires: expires,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), token.ValidTo,
            new SignedInAgent(agent.Id, agent.FullName, agent.Email));
    }

    [HttpGet("me")]
    public async Task<ActionResult<SignedInAgent>> Me(CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)) return Unauthorized();
        var agent = await agents.GetByIdAsync(id, ct);
        if (agent is null || !agent.IsActive) return Unauthorized();
        return new SignedInAgent(agent.Id, agent.FullName, agent.Email);
    }
}
