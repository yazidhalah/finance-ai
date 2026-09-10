using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace FinanceAi.Infrastructure.Security;

/// <summary>
/// Argon2id password hashing at the parameters SEC-01 requires: m=64MB, t=3, p=1, as a minimum.
/// Not MD5, not SHA, not bcrypt-at-a-low-cost. The encoded form is the standard PHC string, so the
/// parameters travel with the hash and can be raised later without invalidating existing passwords.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string encoded);
}

public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    /// <summary>SEC-01 minimum, in KiB.</summary>
    public const int MemoryKib = 65536;

    public const int Iterations = 3;
    public const int Parallelism = 1;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    /// <summary>SEC-01: minimum length, no composition rules, no forced rotation.</summary>
    public const int MinimumPasswordLength = 12;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt);

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}$" +
            $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public bool Verify(string password, string encoded)
    {
        if (password is null || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        if (!TryParse(encoded, out var parameters, out var salt, out var expected))
        {
            return false;
        }

        var actual = Derive(password, salt, parameters.MemoryKib, parameters.Iterations, parameters.Parallelism);

        // Constant time: a timing difference here is a password oracle (SEC-06).
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(
        string password,
        byte[] salt,
        int memoryKib = MemoryKib,
        int iterations = Iterations,
        int parallelism = Parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(HashBytes);
    }

    internal static bool TryParse(
        string encoded,
        out (int MemoryKib, int Iterations, int Parallelism) parameters,
        out byte[] salt,
        out byte[] hash)
    {
        parameters = default;
        salt = [];
        hash = [];

        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != "v=19")
        {
            return false;
        }

        var settings = parts[3].Split(',');
        if (settings.Length != 3 ||
            !TryReadSetting(settings[0], "m=", out var memory) ||
            !TryReadSetting(settings[1], "t=", out var iterations) ||
            !TryReadSetting(settings[2], "p=", out var parallelism))
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[4]);
            hash = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        parameters = (memory, iterations, parallelism);
        return true;
    }

    private static bool TryReadSetting(string setting, string prefix, out int value)
    {
        value = 0;
        return setting.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(setting.AsSpan(prefix.Length), out value);
    }
}
