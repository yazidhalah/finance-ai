using System.Security.Cryptography;
using System.Text;

namespace FinanceAi.Infrastructure.Security;

/// <summary>Encryption at rest for the TOTP secret (SEC-02): AES-256-GCM under a key-encryption key from the environment.</summary>
public interface ISecretBox
{
    bool Available { get; }

    byte[] Seal(ReadOnlySpan<byte> plaintext);

    byte[] Open(ReadOnlySpan<byte> sealedBytes);

    /// <summary>True when the envelope is under anything but the current key (a previous key, or the slice 13 headerless layout) — slice 17.</summary>
    bool NeedsReseal(ReadOnlySpan<byte> sealedBytes);

    /// <summary>True when the envelope can be opened by a key this box holds — false means the row would lock its user out.</summary>
    bool CanOpen(ReadOnlySpan<byte> sealedBytes);
}

/// <summary>
/// <c>MFA_KEK_BASE64</c>: 32 bytes, base64; <c>MFA_KEK_BASE64_PREVIOUS</c>: optional, the key being retired (slice 17).
/// Never in source (SEC-67). Without a current key MFA enrolment answers <c>mfa_unavailable</c> rather than storing a
/// secret in the clear.
/// <para>
/// Envelope (slice 17): <c>FKEK1 · kid(8) · nonce(12) · tag(16) · ciphertext</c>, where <c>kid</c> is the first eight
/// bytes of SHA-256 over the key. The slice 13 layout (<c>nonce · tag · ciphertext</c>, no header) is still opened —
/// with the current key first, then the previous — so no row written before this slice is unreadable.
/// </para>
/// </summary>
public sealed class AesGcmSecretBox : ISecretBox
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FKEK1");
    private const int KidLength = 8;
    private static readonly int NonceLength = AesGcm.NonceByteSizes.MaxSize;
    private static readonly int TagLength = AesGcm.TagByteSizes.MaxSize;

    private readonly byte[]? key;
    private readonly byte[]? kid;
    private readonly byte[]? previousKey;
    private readonly byte[]? previousKid;

    public AesGcmSecretBox() : this(Environment.GetEnvironmentVariable("MFA_KEK_BASE64"), Environment.GetEnvironmentVariable("MFA_KEK_BASE64_PREVIOUS"))
    {
    }

    public AesGcmSecretBox(string? base64Key, string? base64PreviousKey = null)
    {
        this.key = Parse(base64Key, "MFA_KEK_BASE64");
        this.previousKey = Parse(base64PreviousKey, "MFA_KEK_BASE64_PREVIOUS");
        this.kid = this.key is null ? null : KidOf(this.key);
        this.previousKid = this.previousKey is null ? null : KidOf(this.previousKey);
    }

    public bool Available => this.key is not null;

    /// <summary>The current key's id, hex — what a freshly sealed envelope carries.</summary>
    public string? CurrentKeyId => this.kid is null ? null : Convert.ToHexStringLower(this.kid);

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (this.key is null || this.kid is null) throw new InvalidOperationException("MFA key not configured.");
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = new byte[TagLength];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(this.key, TagLength);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. Magic, .. this.kid, .. nonce, .. tag, .. cipher];
    }

    public byte[] Open(ReadOnlySpan<byte> sealedBytes)
    {
        if (this.key is null) throw new InvalidOperationException("MFA key not configured.");
        if (HasHeader(sealedBytes))
        {
            var envelopeKid = sealedBytes.Slice(Magic.Length, KidLength);
            var k = envelopeKid.SequenceEqual(this.kid) ? this.key
                : this.previousKid is not null && envelopeKid.SequenceEqual(this.previousKid) ? this.previousKey
                : throw new CryptographicException("The envelope is under a key this host does not hold.");
            return OpenWith(k!, sealedBytes[(Magic.Length + KidLength)..]);
        }

        // Slice 13 layout: no key id. The current key first; the previous key if the current one rejects the tag.
        try
        {
            return OpenWith(this.key, sealedBytes);
        }
        catch (CryptographicException) when (this.previousKey is not null)
        {
            return OpenWith(this.previousKey, sealedBytes);
        }
    }

    public bool NeedsReseal(ReadOnlySpan<byte> sealedBytes) =>
        !HasHeader(sealedBytes) || !sealedBytes.Slice(Magic.Length, KidLength).SequenceEqual(this.kid);

    public bool CanOpen(ReadOnlySpan<byte> sealedBytes)
    {
        if (this.key is null) return false;
        try
        {
            this.Open(sealedBytes);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool HasHeader(ReadOnlySpan<byte> sealedBytes) =>
        sealedBytes.Length >= Magic.Length + KidLength + AesGcm.NonceByteSizes.MaxSize + AesGcm.TagByteSizes.MaxSize && sealedBytes[..Magic.Length].SequenceEqual(Magic);

    private static byte[] OpenWith(byte[] k, ReadOnlySpan<byte> body)
    {
        if (body.Length < NonceLength + TagLength) throw new CryptographicException("Sealed value too short.");
        var nonce = body[..NonceLength];
        var tag = body.Slice(NonceLength, TagLength);
        var cipher = body[(NonceLength + TagLength)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(k, TagLength);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[]? Parse(string? base64, string name)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        var bytes = Convert.FromBase64String(base64.Trim());
        if (bytes.Length != 32) throw new InvalidOperationException($"{name} must decode to exactly 32 bytes.");
        return bytes;
    }

    private static byte[] KidOf(byte[] k) => SHA256.HashData(k)[..KidLength];
}
