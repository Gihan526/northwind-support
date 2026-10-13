using FluentValidation;

namespace SupportDesk.Application.Contracts.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record SignedInAgent(int Id, string FullName, string Email);
public sealed record LoginResponse(string AccessToken, DateTime ExpiresAtUtc, SignedInAgent Agent);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(request => request.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(request => request.Password).NotNull().Length(1, 256);
    }
}
