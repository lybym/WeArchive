using System.Text;
using ZstdSharp;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>
/// Decodes a stored message payload. WeChat 4.x compresses payloads with Zstandard and
/// marks that with a non-zero value in <c>WCDB_CT_message_content</c>; uncompressed
/// payloads are plain UTF-8.
/// </summary>
internal static class WeChatContentDecoder
{
    private const int MaxDecompressedBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Returns the decoded text, or null when the payload is genuinely absent or cannot be
    /// decoded. A decode failure is reported to the caller as a partial record rather than
    /// being treated as empty content.
    /// </summary>
    public static bool TryDecode(byte[]? raw, int compression, out string? text, out string? failureCode)
    {
        text = null;
        failureCode = null;

        if (raw is null || raw.Length == 0)
        {
            return true;
        }

        if (compression != 0)
        {
            try
            {
                using var decompressor = new Decompressor();
                var decoded = decompressor.Unwrap(raw, MaxDecompressedBytes);
                text = Encoding.UTF8.GetString(decoded);
                return true;
            }
            catch (ZstdException)
            {
                failureCode = Core.Domain.DiagnosticCodes.ContentDecompressionFailed;
                return false;
            }
            catch (ArgumentException)
            {
                failureCode = Core.Domain.DiagnosticCodes.ContentDecompressionFailed;
                return false;
            }
        }

        text = Encoding.UTF8.GetString(raw);
        return true;
    }
}
