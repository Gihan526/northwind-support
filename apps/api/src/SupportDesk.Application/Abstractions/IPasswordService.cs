namespace SupportDesk.Application.Abstractions;

// Keeps the application/domain independent of the password hashing library.
public interface IPasswordService
{
    string Hash(string email, string password);
    bool Verify(string email, string hash, string password);
}
