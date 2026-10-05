using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KodosadStudio;

public static class AccountSecurity
{
    public const int Iterations = 600_000;
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password123!", "Qwerty123!", "Admin123!", "Welcome123!", "Aa123456!", "12345678"
    };

    public static void ValidateEmail(string value)
    {
        var email = value.Trim();
        if (email.Length is < 7 or > 254 || !Regex.IsMatch(email, @"^[^\s@.]+@[^\s@.]+(?:\.[^\s@.]+)+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Введите почту в формате имя@домен.домен.");
        try
        {
            if (!new MailAddress(email).Address.Equals(email, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Введите корректный адрес электронной почты.");
        }
        catch (FormatException) { throw new ArgumentException("Введите корректный адрес электронной почты."); }
    }

    public static void ValidateUserName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 3 or > 40 || !Regex.IsMatch(name, @"^[\p{L}\p{N}_.-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Имя пользователя: от 3 до 40 букв, цифр или знаков . _ -.");
    }

    public static void ValidateDisplayName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 2 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Отображаемое имя должно содержать от 2 до 80 символов.");
    }

    public static void ValidatePassword(string value)
    {
        if (value.Length < 8 || value.Length > 128 ||
            !value.Any(char.IsUpper) || !value.Any(char.IsLower) || !value.Any(char.IsDigit) ||
            !value.Any(c => !char.IsLetterOrDigit(c)) || CommonPasswords.Contains(value))
            throw new ArgumentException("Пароль должен содержать не менее 8 символов, заглавную и строчную буквы, цифру и знак; простые пароли запрещены.");
    }

    public static (byte[] Salt, byte[] Hash) CreatePasswordHash(string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (salt, hash);
    }

    public static bool VerifyPassword(string password, byte[] salt, byte[] expectedHash, int iterations)
    {
        if (iterations is < 100_000 or > 2_000_000 || salt.Length != 16 || expectedHash.Length != 32) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        try { return CryptographicOperations.FixedTimeEquals(actual, expectedHash); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    public static string NormalizeEmail(string value) => value.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    public static string NormalizeUserName(string value) => value.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
}

public sealed record UserSession(int UserId, string Email, string UserName, string DisplayName);
public sealed record PasswordResetRequest(int UserId, string Email, string Code);
public sealed record EmailVerificationRequest(int UserId, string Email, string DisplayName, string Code);
public sealed record SavedProject(int ProjectId, string Name, string Algorithm, string Source, DateTime UpdatedAtUtc);
public sealed record SavedAnalysisRun(long RunId, string ProjectName, string Algorithm, int SourceCharacters,
    int SourceBytes, int OutputBits, double Entropy, double AverageCodeLength, double RatioPercent,
    string Source, string Output, DateTime CreatedAtUtc);

public sealed class AccountException(string message) : Exception(message);
