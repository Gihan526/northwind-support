using Microsoft.AspNetCore.Identity;
using SupportDesk.Application.Abstractions;

namespace SupportDesk.Infrastructure.Services;

public sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<string> _hasher = new();
    public string Hash(string email, string password) => _hasher.HashPassword(email, password);
    public bool Verify(string email, string hash, string password) =>
        _hasher.VerifyHashedPassword(email, hash, password) != PasswordVerificationResult.Failed;
}
