using System.Security.Cryptography;

namespace WeArchive.Infrastructure.WeChat.Crypto;

/// <summary>
/// SQLCipher 4 page format, as used by the WeChat 4.x local databases.
/// <code>
/// [0:16)    salt          (page 1 only; random per database file)
/// [16:4016) encrypted payload for page 1, [0:4016) for every other page
/// [4016:4032) AES-256-CBC IV
/// [4032:4096) HMAC-SHA512
/// </code>
/// <para>
/// Key handling observed on WeChat 4.1.x: the key recovered from the client's WCDB
/// cipher configuration is a <em>raw</em> 32-byte key (no passphrase KDF), and the
/// page HMAC excludes the file salt. Both facts are verified at runtime against the
/// real page-1 HMAC before any decryption is attempted, so a mismatched assumption
/// fails closed instead of producing garbage.
/// </para>
/// <para>
/// Derivation of the separate HMAC key from the raw key is the standard SQLCipher rule
/// <c>PBKDF2-HMAC-SHA512(encKey, salt ^ 0x3a, 2, 32)</c>.
/// </para>
/// </summary>
internal static class SqlCipherPageCipher
{
    public const int PageSize = 4096;
    public const int ReserveSize = 80;
    public const int HmacSize = 64;
    public const int SaltSize = 16;
    public const int IvOffset = PageSize - ReserveSize;
    public const int HmacOffset = IvOffset + 16;
    public const int RawKeySize = 32;

    public static byte[] DeriveMacKey(ReadOnlySpan<byte> encryptionKey, ReadOnlySpan<byte> salt)
    {
        Span<byte> macSalt = stackalloc byte[SaltSize];
        for (var i = 0; i < SaltSize; i++)
        {
            macSalt[i] = (byte)(salt[i] ^ 0x3A);
        }

        return Rfc2898DeriveBytes.Pbkdf2(encryptionKey, macSalt, 2, HashAlgorithmName.SHA512, 32);
    }

    /// <summary>
    /// Verifies a page against its stored HMAC. Page 1 covers the ciphertext and IV but
    /// not the 16-byte salt; page <c>n</c> covers the whole page body plus the page number.
    /// </summary>
    public static bool VerifyPage(ReadOnlySpan<byte> macKey, ReadOnlySpan<byte> page, uint pageNumber)
    {
        if (page.Length < PageSize)
        {
            return false;
        }

        var start = pageNumber == 1 ? SaltSize : 0;
        var bodyLength = HmacOffset - start;
        var body = new byte[bodyLength + 4];
        page.Slice(start, bodyLength).CopyTo(body);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(bodyLength), pageNumber);

        var computed = HMACSHA512.HashData(macKey, body);
        return CryptographicOperations.FixedTimeEquals(
            computed,
            page.Slice(HmacOffset, HmacSize));
    }

    /// <summary>Decrypts one page in place-equivalent fashion, returning the plaintext page.</summary>
    public static byte[] DecryptPage(ReadOnlySpan<byte> encryptionKey, ReadOnlySpan<byte> page, uint pageNumber)
    {
        var plaintext = new byte[PageSize];
        var iv = page.Slice(IvOffset, 16).ToArray();

        using var aes = Aes.Create();
        aes.Key = encryptionKey.ToArray();
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();

        if (pageNumber == 1)
        {
            // The leading 16 bytes of a page-1 payload are the file salt, which is not
            // encrypted; the plaintext SQLite header replaces it.
            var payloadLength = IvOffset - SaltSize;
            var cipher = page.Slice(SaltSize, payloadLength).ToArray();
            var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            "SQLite format 3\0"u8.CopyTo(plaintext);
            plain.CopyTo(plaintext.AsSpan(SaltSize));
        }
        else
        {
            var cipher = page[..IvOffset].ToArray();
            var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            plain.CopyTo(plaintext);
        }

        // The reserved tail is zeroed: SQLite never reads it, but zeroing makes the
        // decrypted image deterministic.
        return plaintext;
    }
}
