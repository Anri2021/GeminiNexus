using System.Security.Cryptography;
namespace GeminiNexus.Server.Security;
public static class Passwords
{
    private const int Iterations = 210000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('.');
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), int.Parse(parts[0]), HashAlgorithmName.SHA512, 32);
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(parts[2]));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException) { return false; }
    }
}
