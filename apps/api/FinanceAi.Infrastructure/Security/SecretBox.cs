using System.Security.Cryptography;

namespace FinanceAi.Infrastructure.Security;

/// <summary>Encryption at rest for the TOTP secret (SEC-02): AES-256-GCM under a key-encryption key from the environment.</summary>
public interface ISecretBox
{
    bool Available { get; }

    byte[] Seal(ReadOnlySpan<byte> plaintext);

    byte[] Open(ReadOnlySpan<byte> sealedBytes);
}

/// <summary>
/// <c>MFA_KEK_BASE64</c>: 32 bytes, base64. Never in source (SEC-67). Without it MFA enrolment answers
/// <c>mfa_unavailable</c> rather than storing a secret in the clear. Layout: 12-byte nonce · 16-byte tag · ciphertext.
/// </summary>
public sealed class AesGcmSecretBox : ISecretBox
{
    private readonly byte[]? key;

    public AesGcmSecretBox() : this(Environment.GetEnvironmentVariable("MFA_KEK_BASE64"))
    {
    }

    public AesGcmSecretBox(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key)) return;
        var bytes = Convert.FromBase64String(base64Key.Trim());
        if (bytes.Length != 32) throw new InvalidOperationException("MFA_KEK_BASE64 must decode to exactly 32 bytes.");
        this.key = bytes;
    }

    public bool Available => this.key is not null;

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (this.key is null) throw new InvalidOperationException("MFA key not configured.");
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(this.key, tag.Length);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Open(ReadOnlySpan<byte> sealedBytes)
    {
        if (this.key is null) throw new InvalidOperationException("MFA key not configured.");
        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        if (sealedBytes.Length < nonceLength + tagLength) throw new CryptographicException("Sealed value too short.");
        var nonce = sealedBytes[..nonceLength];
        var tag = sealedBytes.Slice(nonceLength, tagLength);
        var cipher = sealedBytes[(nonceLength + tagLength)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(this.key, tagLength);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
