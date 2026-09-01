using Microsoft.AspNetCore.Identity;

namespace Landbridge.ControlPlane.Auth;

/// <summary>
/// Operator passphrase at rest: ASP.NET Identity's PBKDF2 hasher (V3), not SHA-256.
/// The config value is an Identity password hash, never the plaintext. A leftover
/// SHA-256 hex is treated as unconfigured so the door fail-closes (503) rather than
/// rejecting every login as a wrong guess.
/// </summary>
public static class OperatorPassphrase
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object User = new();

    public static string Hash(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        return Hasher.HashPassword(User, passphrase);
    }

    public static bool LooksConfigured(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return false;
        stored = stored.Trim();
        // Old SHA-256 hex (64 chars) is not a password hash.
        if (stored.Length == 64 && IsHex(stored))
            return false;
        try
        {
            var bytes = Convert.FromBase64String(stored);
            return bytes.Length > 13 && bytes[0] is 0x00 or 0x01;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool Verify(string storedHash, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || !LooksConfigured(storedHash))
            return false;
        var result = Hasher.VerifyHashedPassword(User, storedHash.Trim(), passphrase);
        return result is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                return false;
        }
        return true;
    }
}
