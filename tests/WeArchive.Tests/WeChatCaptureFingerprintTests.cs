using WeArchive.Infrastructure.WeChat;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

public sealed class WeChatCaptureFingerprintTests
{
    [Fact]
    public async Task FingerprintTracksDatabaseAndWalContentButIgnoresShm()
    {
        using var temp = new TempDirectory();
        var database = temp.Combine("message_0.db");
        await File.WriteAllBytesAsync(database, [1, 2, 3]);

        var initial = await WeChatCaptureAdapter.FingerprintDatabaseAsync(database, CancellationToken.None);
        Assert.Equal(initial,
            await WeChatCaptureAdapter.FingerprintDatabaseAsync(database, CancellationToken.None));

        await File.WriteAllBytesAsync(database + "-shm", [9, 8, 7]);
        Assert.Equal(initial,
            await WeChatCaptureAdapter.FingerprintDatabaseAsync(database, CancellationToken.None));

        await File.WriteAllBytesAsync(database + "-wal", [4, 5, 6]);
        var withWal = await WeChatCaptureAdapter.FingerprintDatabaseAsync(database, CancellationToken.None);
        Assert.NotEqual(initial, withWal);

        await File.WriteAllBytesAsync(database + "-wal", [4, 5, 7]);
        Assert.NotEqual(withWal,
            await WeChatCaptureAdapter.FingerprintDatabaseAsync(database, CancellationToken.None));

        await File.WriteAllBytesAsync(database, [1, 2, 4]);
        Assert.NotEqual(initial,
            await WeChatCaptureAdapter.FingerprintDatabaseAsync(database, CancellationToken.None));
    }

    [Fact]
    public async Task MissingDatabaseCannotBeMistakenForUnchangedPartition()
    {
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            WeChatCaptureAdapter.FingerprintDatabaseAsync(temp.Combine("missing.db"), CancellationToken.None));
    }
}
