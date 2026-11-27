using System.Security.Cryptography;

namespace MSSQL_Server.Security
{
    internal static class PasswordHasher
    {
        private const int Iterations = 210_000;

        public static (string Salt, string Hash) Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        public static bool Verify(string password, string saltBase64, string hashBase64)
        {
            try
            {
                byte[] salt = Convert.FromBase64String(saltBase64);
                byte[] expected = Convert.FromBase64String(hashBase64);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                    password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
