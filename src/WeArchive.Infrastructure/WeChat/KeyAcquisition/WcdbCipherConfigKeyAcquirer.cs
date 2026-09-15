using System.Runtime.Versioning;
using System.Text;
using WeArchive.Infrastructure.WeChat.Crypto;

namespace WeArchive.Infrastructure.WeChat.KeyAcquisition;

/// <summary>
/// Recovers the local WeChat database keys that the running client holds in memory.
/// Implementations must be read-only towards the client process.
/// </summary>
internal interface IWeChatDatabaseKeyAcquirer
{
    string Name { get; }

    /// <summary>
    /// Attempts to recover keys for the supplied encrypted databases.
    /// <paramref name="databasePaths"/> is used to <em>verify</em> candidate keys, so a
    /// candidate that does not open a real database is never reported.
    /// </summary>
    WeChatDatabaseKeyAcquisitionResult Acquire(IReadOnlyList<string> databasePaths);
}

internal sealed record WeChatDatabaseKeyAcquisitionResult(
    WeChatKeySet? KeySet,
    bool Succeeded,
    string Message)
{
    public static WeChatDatabaseKeyAcquisitionResult Failure(string message) => new(null, false, message);

    public static WeChatDatabaseKeyAcquisitionResult Success(WeChatKeySet keys, string message) =>
        new(keys, true, message);
}

/// <summary>
/// Recovers keys from the WCDB cipher configuration cached in the client's heap.
/// <para>
/// Since WeChat 4.1.11 the plaintext <c>x'&lt;96 hex&gt;'</c> literal is no longer stored
/// as a readable string; the same literal is held in an obfuscated buffer, so a plain
/// string search finds nothing. The obfuscation is a repeating XOR mask, which makes the
/// literal recoverable by scanning process memory for the masked form of the
/// well-known <c>x'</c> prefix and validating each decoded candidate against a real
/// database page-1 HMAC.
/// </para>
/// <para>
/// This deliberately does not depend on a version-specific code offset or on code
/// injection, so it is not tied to one Weixin.dll build. See
/// docs/research/wechat-4x-windows-key-acquisition.md for sources and licensing notes.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WcdbCipherConfigKeyAcquirer : IWeChatDatabaseKeyAcquirer
{
    /// <summary>Repeating 32-byte mask applied to the cached WCDB cipher literal.</summary>
    private static readonly byte[] Mask = Convert.FromHexString(
        "d2c7442458020000004889442450488b450048844c2448488944254048584c24");

    private const int LiteralLength = 99; // x' + 96 hex + '
    private const int HexLength = 96;

    public string Name => "wcdb-cipher-config";

    public WeChatDatabaseKeyAcquisitionResult Acquire(IReadOnlyList<string> databasePaths)
    {
        ArgumentNullException.ThrowIfNull(databasePaths);

        var processIds = ProcessMemoryReader.FindProcessIds(WeChatClient.WeChatProcessName);
        if (processIds.Count == 0)
        {
            return WeChatDatabaseKeyAcquisitionResult.Failure(
                $"The WeChat client process ({WeChatClient.WeChatProcessName}) is not running. " +
                "Start WeChat and sign in, then retry.");
        }

        var found = new Dictionary<string, WeChatDatabaseKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var processId in processIds)
        {
            ProcessMemoryReader.EnumerateReadableMemory(processId, (buffer, _) =>
                ScanBuffer(buffer, found));
        }

        if (found.Count == 0)
        {
            return WeChatDatabaseKeyAcquisitionResult.Failure(
                "No database key could be recovered from the WeChat process. The installed client version " +
                "may use a key layout this build does not recognise.");
        }

        var verifier = new DatabaseVerifier(databasePaths);
        var verified = found.Values.Where(k => verifier.Verifies(k.Key)).ToList();

        if (verified.Count == 0)
        {
            return WeChatDatabaseKeyAcquisitionResult.Failure(
                $"Recovered {found.Count} candidate key(s) but none opens a local database.");
        }

        return WeChatDatabaseKeyAcquisitionResult.Success(
            new WeChatKeySet(verified),
            $"Recovered {verified.Count} verified database key(s).");
    }

    private static void ScanBuffer(byte[] buffer, Dictionary<string, WeChatDatabaseKey> found)
    {
        // The masked literal begins with the masked bytes of 'x' and '\''. Searching for
        // that 2-byte needle and then validating the whole literal is far cheaper than
        // decoding every offset in the region. Because the mask has a 32-byte period the
        // search is repeated once per phase.
        var span = buffer.AsSpan();
        var limit = span.Length - LiteralLength;
        if (limit < 0)
        {
            return;
        }

        Span<byte> needle = stackalloc byte[2];
        for (var phase = 0; phase < Mask.Length; phase++)
        {
            needle[0] = (byte)('x' ^ Mask[phase]);
            needle[1] = (byte)('\'' ^ Mask[(phase + 1) % Mask.Length]);

            var position = 0;
            while (position <= limit)
            {
                var relative = span[position..(limit + 2)].IndexOf(needle);
                if (relative < 0)
                {
                    break;
                }

                var index = position + relative;
                position = index + 1;

                var hex = TryDecode(span.Slice(index, LiteralLength), phase);
                if (hex is null)
                {
                    continue;
                }

                found.TryAdd(hex[64..], new WeChatDatabaseKey(
                    hex[64..],
                    Convert.FromHexString(hex[..64])));
            }
        }
    }

    private static string? TryDecode(ReadOnlySpan<byte> masked, int phase)
    {
        Span<char> decoded = stackalloc char[LiteralLength];
        for (var k = 0; k < LiteralLength; k++)
        {
            var value = (byte)(masked[k] ^ Mask[(phase + k) % Mask.Length]);
            decoded[k] = (char)value;
        }

        if (decoded[0] != 'x' || decoded[1] != '\'' || decoded[^1] != '\'')
        {
            return null;
        }

        var hex = new string(decoded[2..^1]);
        return hex.Length == HexLength && IsHex(hex) ? hex.ToLowerInvariant() : null;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies a candidate key by deriving its HMAC key for each real database salt and
    /// checking the stored page-1 HMAC. This is what makes a recovered key trustworthy.
    /// </summary>
    private sealed class DatabaseVerifier
    {
        private readonly List<(byte[] Salt, byte[] Page1)> _pages = [];

        public DatabaseVerifier(IReadOnlyList<string> databasePaths)
        {
            foreach (var path in databasePaths)
            {
                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length < SqlCipherPageCipher.PageSize)
                    {
                        continue;
                    }

                    var page = new byte[SqlCipherPageCipher.PageSize];
                    stream.ReadExactly(page, 0, page.Length);

                    // Plaintext databases and files whose salt is all zero are not encrypted.
                    if (page.AsSpan(0, 15).SequenceEqual("SQLite format 3"u8))
                    {
                        continue;
                    }

                    _pages.Add((page.AsSpan(0, 16).ToArray(), page));
                }
                catch (IOException)
                {
                    // A locked or vanished file simply does not participate in verification.
                }
            }
        }

        public bool Verifies(byte[] key)
        {
            foreach (var (salt, page1) in _pages)
            {
                var macKey = SqlCipherPageCipher.DeriveMacKey(key, salt);
                if (SqlCipherPageCipher.VerifyPage(macKey, page1, 1))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
