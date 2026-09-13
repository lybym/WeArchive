using System.Globalization;
using WeArchive.Infrastructure.WeChat.Crypto;

namespace WeArchive.Infrastructure.WeChat.KeyAcquisition;

/// <summary>One recovered raw SQLCipher key, identified by the database salt it belongs to.</summary>
internal sealed record WeChatDatabaseKey(string SaltHex, byte[] Key)
{
    public override string ToString() => $"salt={SaltHex} key=<redacted>";
}

/// <summary>
/// The set of raw database keys recovered for a local WeChat installation.
/// <para>
/// WeChat 4.1.x derives a distinct key per database file, so a key is identified by the
/// 16-byte salt stored at the head of the encrypted file. Keys are matched to files by
/// salt and then <em>verified</em> against the real page-1 HMAC; an unverifiable key is
/// never used.
/// </para>
/// </summary>
internal sealed class WeChatKeySet(IReadOnlyList<WeChatDatabaseKey> keys)
{
    private readonly Dictionary<string, byte[]> _bySalt =
        keys.GroupBy(k => k.SaltHex, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WeChatDatabaseKey> Keys { get; } = keys;

    public int Count => _bySalt.Count;

    /// <summary>All candidate keys whose salt was not matched, used as a last-resort probe.</summary>
    private IReadOnlyList<byte[]> UnmatchedKeys => [.. _bySalt.Values];

    public bool TryResolve(string databasePath, out byte[] key)
    {
        key = [];
        byte[]? header;
        try
        {
            header = ReadHeader(databasePath);
        }
        catch (IOException)
        {
            return false;
        }

        if (header is null || header.Length < SqlCipherPageCipher.PageSize)
        {
            return false;
        }

        var saltHex = Convert.ToHexString(header.AsSpan(0, SqlCipherPageCipher.SaltSize)).ToLowerInvariant();

        if (_bySalt.TryGetValue(saltHex, out var bySalt) && Matches(bySalt, header))
        {
            key = bySalt;
            return true;
        }

        // Fall back to probing every key: covers a database whose salt was not present in
        // the cipher configuration we could observe.
        foreach (var candidate in UnmatchedKeys)
        {
            if (Matches(candidate, header))
            {
                key = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool Matches(byte[] candidate, byte[] page1)
    {
        var macKey = SqlCipherPageCipher.DeriveMacKey(candidate, page1.AsSpan(0, SqlCipherPageCipher.SaltSize));
        return SqlCipherPageCipher.VerifyPage(macKey, page1, 1);
    }

    private static byte[]? ReadHeader(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(SqlCipherPageCipher.PageSize, (int)Math.Min(stream.Length, SqlCipherPageCipher.PageSize))];
        stream.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }

    public string DescribeKeyCount() => Count.ToString(CultureInfo.InvariantCulture);
}
