using System.Security.Cryptography;
using System.Text;

namespace Saehakgi.Core.Crypto;

/// <summary>
/// Derives a 256-bit symmetric key from a user passphrase.
/// Scaffold uses PBKDF2-SHA256 (BCL, zero external deps). SPEC target is Argon2id
/// (see SPEC.md §4); swap by adding an Argon2id derivation and bumping the KDF id
/// byte in the bundle header — the rest of the format is unaffected.
/// </summary>
public static class PassphraseCrypto
{
    public const int KeySizeBytes = 32;   // AES-256
    public const int SaltSizeBytes = 16;
    public const int Pbkdf2Iterations = 600_000;

    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));

        return Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(passphrase),
            salt: salt,
            iterations: iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: KeySizeBytes);
    }

    public static byte[] NewSalt()
    {
        var salt = new byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}
