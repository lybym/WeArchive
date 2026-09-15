using WeArchive.Infrastructure.WeChat;

namespace WeArchive.Tests.Support;

/// <summary>
/// A fact that only runs when a usable local WeChat installation is present.
/// <para>
/// docs/DEVELOPMENT.md section 6 requires adapter-compatibility tests to be isolated and to
/// skip gracefully when the required local environment is unavailable, so the suite stays
/// green on a machine (or CI runner) without WeChat.
/// </para>
/// </summary>
public sealed class WeChatEnvironmentFactAttribute : FactAttribute
{
    public WeChatEnvironmentFactAttribute()
    {
        if (!WeChatTestEnvironment.IsAvailable(out var reason))
        {
            Skip = reason;
        }
    }
}

internal static class WeChatTestEnvironment
{
    public static bool IsAvailable(out string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = "Windows-only adapter.";
            return false;
        }

        if (WeChatDataLocator.Discover().Count == 0)
        {
            reason = "No local WeChat data directory was found on this machine.";
            return false;
        }

        if (!WeChatClient.IsRunning())
        {
            reason = "WeChat is not running, so the local archive key cannot be recovered.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
