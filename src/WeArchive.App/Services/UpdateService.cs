using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace WeArchive.App.Services;

/// <summary>
/// "Check for updates" only. docs/adr/0004-distribution-velopack.md.
/// <para>
/// The update source is the project's GitHub Releases. When that release feed does not
/// exist yet, or the network is unavailable, the check reports a normal message instead of
/// failing: an unreachable update source must never break the application.
/// </para>
/// <para>
/// The application also runs unpackaged during development, where no update feed applies at
/// all; that case is reported as such rather than as an error.
/// </para>
/// </summary>
public sealed class UpdateService(string repositoryUrl)
{
    public const string RepositoryUrl = "https://github.com/lybym/WeArchive";

    private readonly string _repositoryUrl = repositoryUrl;

    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(_repositoryUrl, accessToken: null, prerelease: false));

            if (!manager.IsInstalled)
            {
                return "当前为开发/绿色版运行，未安装 Velopack 更新源，跳过更新检查。";
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return $"已是最新版本（当前 {manager.CurrentVersion}）。";
            }

            return $"发现新版本 {update.TargetFullRelease.Version}（当前 {manager.CurrentVersion}）。"
                + "请在 GitHub Releases 下载安装包完成升级。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Covers a missing release feed, offline machines and HTTP errors alike.
            return $"暂时无法检查更新：{ex.Message}";
        }
    }
}
