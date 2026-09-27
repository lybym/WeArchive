using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat;

namespace WeArchive.Infrastructure;

/// <summary>Reconstructs a fresh canonical archive exclusively from verified Raw Vault evidence.</summary>
public sealed class RebuildService(IRawVaultStore rawVault, string archivePath, IClock clock)
{
    private readonly IRawVaultStore _rawVault = rawVault ?? throw new ArgumentNullException(nameof(rawVault));
    private readonly string _archivePath = Path.GetFullPath(archivePath);
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<RebuildResult> RebuildAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_rawVault is not RawVaultStore store)
            throw new NotSupportedException("The configured Raw Vault store cannot enumerate captured accounts.");

        var accountIds = await store.ListAccountIdsAsync(cancellationToken).ConfigureAwait(false);
        if (accountIds.Count == 0)
            throw new InvalidOperationException("No Raw Vault accounts are available to rebuild.");

        var directory = Path.GetDirectoryName(_archivePath)!;
        Directory.CreateDirectory(directory);
        var stagingPath = _archivePath + ".rebuild-" + Guid.NewGuid().ToString("N") + ".db";
        var fresh = new SqliteArchiveStore(stagingPath, _clock);
        var skipped = new List<RebuildSkippedAccount>();
        try
        {
            await fresh.InitializeAsync(cancellationToken).ConfigureAwait(false);
            foreach (var accountId in accountIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latest = await _rawVault.GetLatestGenerationAsync(accountId, cancellationToken).ConfigureAwait(false);
                if (latest is null)
                {
                    // An account directory can exist without any published generation: a capture that
                    // failed or was cancelled leaves the account/generations scaffold behind. Such an
                    // account has no evidence to rebuild, so it is reported and skipped instead of
                    // aborting the rebuild for every other account (Issue #37). A generation that
                    // exists but cannot be read still fails hard below.
                    skipped.Add(new RebuildSkippedAccount(
                        accountId,
                        "The Raw Vault account has no published generation."));
                    progress?.Report($"Skipping {accountId}: no published generation");
                    continue;
                }

                var generation = await _rawVault.OpenGenerationAsync(accountId, latest.GenerationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Raw Vault generation '{latest.GenerationId}' failed manifest or checksum validation.");
                using var adapter = CapturedWeChatSourceAdapter.Create(generation);
                var account = (await adapter.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Single();
                var expectedAccountId = StableIds.Account(adapter.AdapterName, account.SourceProfileId);
                if (!string.Equals(expectedAccountId, accountId, StringComparison.Ordinal))
                    throw new InvalidDataException("Raw Vault account identity does not match the stable source identity.");

                var descriptor = await adapter.DescribeSourceAsync(cancellationToken).ConfigureAwait(false);
                var conversations = await adapter.ListConversationsAsync(account.SourceProfileId, cancellationToken).ConfigureAwait(false);
                await fresh.UpsertAccountAsync(new ArchiveAccount
                {
                    Id = accountId,
                    SourceProfileId = account.SourceProfileId,
                    AdapterName = descriptor.AdapterName,
                    AdapterVersion = descriptor.AdapterVersion,
                    SourceVersion = descriptor.SourceVersion,
                    DisplayName = account.DisplayName,
                }, cancellationToken).ConfigureAwait(false);
                var sourceParticipants = await adapter.ListParticipantsAsync(account.SourceProfileId, cancellationToken).ConfigureAwait(false);
                await fresh.UpsertParticipantsAsync(sourceParticipants.Select(p => new ArchiveParticipant
                {
                    Id = StableIds.Participant(accountId, p.SourceUserId),
                    AccountId = accountId,
                    SourceParticipantId = p.SourceUserId,
                    LatestRemark = p.Remark,
                    Nickname = p.Nickname,
                    Alias = p.Alias,
                }), cancellationToken).ConfigureAwait(false);
                var importer = new ImportService(adapter, fresh, _clock);
                progress?.Report($"Rebuilding {accountId}: {conversations.Count} conversation(s)");
                foreach (var conversation in conversations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var detail = await adapter.DescribeConversationAsync(account.SourceProfileId, conversation.SourceConversationId, cancellationToken).ConfigureAwait(false);
                    var outcome = await importer.ImportConversationAsync(new ImportRequest
                    {
                        SourceProfileId = account.SourceProfileId,
                        SourceConversationId = conversation.SourceConversationId,
                        Kind = conversation.Kind,
                        PeerSourceUserId = conversation.PeerSourceUserId,
                        ConversationTitle = conversation.Title,
                        TotalHint = detail.MessageCount,
                    }, null, cancellationToken).ConfigureAwait(false);
                    if (outcome.Run.Status != ImportRunStatus.Completed)
                        throw new InvalidDataException($"Rebuild could not completely read conversation '{conversation.SourceConversationId}'.");
                }
                progress?.Report($"Rebuilt {accountId}");
            }

            if (skipped.Count == accountIds.Count)
            {
                // Nothing was rebuildable, so publishing an empty archive would replace a usable
                // canonical archive with an evidently incomplete one. Report explicitly instead.
                throw new InvalidDataException(
                    "No Raw Vault account has a published generation to rebuild: " +
                    string.Join(", ", skipped.Select(s => s.AccountId)) + ".");
            }

            await PreserveUserDisplayNamesAsync(fresh, cancellationToken).ConfigureAwait(false);
            var stats = await fresh.GetArchiveStatsAsync(cancellationToken).ConfigureAwait(false);
            await ValidateIntegrityAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            await ReplaceArchiveAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            return new RebuildResult
            {
                Stats = stats with { ArchivePath = _archivePath },
                SkippedAccounts = skipped,
            };
        }
        finally
        {
            DeleteIfExists(stagingPath);
            DeleteIfExists(stagingPath + "-wal");
            DeleteIfExists(stagingPath + "-shm");
        }
    }

    private async Task PreserveUserDisplayNamesAsync(IArchiveStore fresh, CancellationToken cancellationToken)
    {
        if (!File.Exists(_archivePath))
            return;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _archivePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, account_id, source_participant_id, user_display_name FROM participants WHERE user_display_name IS NOT NULL;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var overrides = new List<ArchiveParticipant>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                overrides.Add(new ArchiveParticipant
                {
                    Id = reader.GetString(0),
                    AccountId = reader.GetString(1),
                    SourceParticipantId = reader.GetString(2),
                    UserDisplayName = reader.GetString(3),
                });
            }
            if (overrides.Count == 0)
                return;

            var rebuiltAccounts = await fresh.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            var rebuiltIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var account in rebuiltAccounts)
            {
                var participants = await fresh.ListParticipantsAsync(account.Id, cancellationToken).ConfigureAwait(false);
                rebuiltIds.UnionWith(participants.Select(p => p.Id));
            }
            await fresh.UpsertParticipantsAsync(overrides.Where(p => rebuiltIds.Contains(p.Id)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ValidateIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The rebuilt canonical database failed SQLite integrity validation.");
        }
    }

    private async Task ReplaceArchiveAsync(string stagingPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_archivePath))
        {
            File.Move(stagingPath, _archivePath);
            return;
        }

        var backupPath = _archivePath + ".rebuild-backup-" + Guid.NewGuid().ToString("N");
        var replaced = false;
        try
        {
            File.Replace(stagingPath, _archivePath, backupPath, ignoreMetadataErrors: true);
            replaced = true;
        }
        catch
        {
            // In-process restoration is best effort; no persistent recovery journal is used.
            if (!File.Exists(_archivePath) && File.Exists(backupPath))
            {
                try { File.Move(backupPath, _archivePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { /* Keep the backup available if restoration cannot complete. */ }
            }
            throw;
        }
        finally
        {
            if (replaced)
                DeleteIfExists(backupPath);
        }
        await Task.CompletedTask;
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}

/// <summary>
/// One Raw Vault account directory that could not contribute to a rebuild because it has no
/// published generation. It is reported rather than fatal, so one leftover directory from a failed
/// capture cannot block rebuilding every other account (docs/CLI.md, Issue #37).
/// </summary>
public sealed record RebuildSkippedAccount(string AccountId, string Reason);

/// <summary>Outcome of a Raw-Vault-only canonical rebuild.</summary>
public sealed record RebuildResult
{
    public required ArchiveStats Stats { get; init; }

    /// <summary>Account directories that were reported and skipped because they have no evidence.</summary>
    public IReadOnlyList<RebuildSkippedAccount> SkippedAccounts { get; init; } = [];
}
