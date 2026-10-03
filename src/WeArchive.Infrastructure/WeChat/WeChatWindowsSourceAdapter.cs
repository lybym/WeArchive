using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure.WeChat.Compatibility;
using WeArchive.Infrastructure.WeChat.KeyAcquisition;
using WeArchive.Infrastructure.WeChat.Parsers;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>
/// The Windows WeChat 4.x local-source adapter.
/// <para>
/// This is the only place that knows about WeChat's client version, database layout,
/// SQLCipher encryption and message wire formats. It is strictly read-only: it opens the
/// client's files with shared read access, derives the local archive key from the running
/// client's own memory, and never writes to, renames or deletes anything under the WeChat
/// data directory.
/// </para>
/// <para>
/// Unrecognised records are emitted as <c>unknown</c> with their upstream type codes
/// preserved. Records whose payload cannot be decompressed, or whose conversation cannot
/// be located, still flow through as partial/unknown events and surface as diagnostics.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WeChatWindowsSourceAdapter : ISourceAdapter, IDisposable
{
    public const string Name = "wechat-windows";

    // Load-bearing bump: RawVaultIngestService skips a conversation only while its checkpoint
    // carries the current reader version, so raising this version invalidates every existing
    // ingest checkpoint and the next ingest re-scans and backfills conversations whose history
    // spans rotated message shards (Issue #71). 0.1.0 read a single rotation window per
    // conversation; 0.2.0 reads all of them.
    public const string Version = "0.2.0";

    private readonly IWeChatDatabaseKeyAcquirer _keyAcquirer;
    private readonly Lock _gate = new();

    private SqlCipherDatabaseCache? _cache;
    private WeChatKeySet? _keys;
    private string? _keyFailure;
    private IReadOnlyList<WeChatInstallation>? _installations;
    private readonly Dictionary<string, WeChatAccountReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SourceAccount? _capturedAccount;
    private readonly SourceDescriptor? _capturedDescriptor;
    private bool _disposed;

    public WeChatWindowsSourceAdapter()
        : this(new WcdbCipherConfigKeyAcquirer())
    {
    }

    internal WeChatWindowsSourceAdapter(IWeChatDatabaseKeyAcquirer keyAcquirer)
    {
        _keyAcquirer = keyAcquirer ?? throw new ArgumentNullException(nameof(keyAcquirer));
    }

    internal WeChatWindowsSourceAdapter(SourceAccount capturedAccount, SourceDescriptor capturedDescriptor, WeChatAccountReader capturedReader, SqlCipherDatabaseCache capturedCache)
        : this(new WcdbCipherConfigKeyAcquirer())
    {
        _capturedAccount = capturedAccount;
        _capturedDescriptor = capturedDescriptor;
        _cache = capturedCache;
        _readers.Add(capturedAccount.SourceProfileId, capturedReader);
    }

    public string AdapterName => Name;

    public string AdapterVersion => Version;

    public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken)
    {
        if (_capturedDescriptor is not null)
            return Task.FromResult(_capturedDescriptor);

        var installations = Discover();
        var (clientVersion, _) = WeChatClient.DetectInstallation();
        var diagnostics = new List<ImportDiagnostic>();

        if (installations.Count == 0)
        {
            return Task.FromResult(new SourceDescriptor
            {
                AdapterName = Name,
                AdapterVersion = Version,
                SourceVersion = clientVersion,
                SourceProductName = WeChatClient.ProductName,
                IsAvailable = false,
                UnavailableReason =
                    "No local WeChat data directory was found. WeArchive reads the current user's own " +
                    "WeChat desktop data; sign in to WeChat for Windows at least once and retry.",
            });
        }

        var accounts = installations.Sum(i => i.Accounts.Count);
        if (!WeChatClient.IsRunning())
        {
            diagnostics.Add(ImportDiagnostic.Partial(
                DiagnosticCodes.SourceNotRunning,
                "WeChat is not running. The local archive key can only be recovered while the client is " +
                "running and signed in, so reading conversations will fail until it is started."));
        }

        return Task.FromResult(new SourceDescriptor
        {
            AdapterName = Name,
            AdapterVersion = Version,
            SourceVersion = clientVersion,
            SourceProductName = WeChatClient.ProductName,
            IsAvailable = true,
            UnavailableReason = null,
            Diagnostics = diagnostics.Count == 0
                ?
                [
                    ImportDiagnostic.Info(
                        DiagnosticCodes.SourceDiscovered,
                        $"Found {accounts} local WeChat account data set(s)."),
                ]
                : diagnostics,
        });
    }

    public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken)
    {
        if (_capturedAccount is not null)
            return Task.FromResult<IReadOnlyList<SourceAccount>>([_capturedAccount]);

        var accounts = Discover()
            .SelectMany(installation => installation.Accounts.Select(account => new SourceAccount
            {
                SourceProfileId = account.SourceProfileId,
                DisplayName = account.SourceProfileId,
                DataRootPath = account.DataDirectory,
                LastActiveAt = account.LastActiveAt,
                IsCurrent = true,
            }))
            .OrderByDescending(a => a.LastActiveAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<SourceAccount>>(accounts);
    }

    public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken)
    {
        var reader = GetReader(sourceProfileId);
        var contacts = reader.ReadContacts();
        var sessions = reader.ReadSessions();
        var results = new List<SourceConversation>(sessions.Count);

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kind = WeChat4Schema.ClassifyConversation(session.UserName);
            contacts.TryGetValue(session.UserName, out var contact);

            results.Add(new SourceConversation
            {
                SourceConversationId = session.UserName,
                Kind = kind,
                Title = ResolveTitle(contact, session.UserName),
                PeerSourceUserId = kind == ConversationKind.Direct ? session.UserName : null,
                LastMessageAt = session.LastTimestamp > 0
                    ? WeChatAccountReader.ToLocalTime(session.LastTimestamp)
                    : null,
            });
        }

        return Task.FromResult<IReadOnlyList<SourceConversation>>(results);
    }

    public Task<SourceConversationDetail> DescribeConversationAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken) =>
        Task.FromResult(GetReader(sourceProfileId).Describe(sourceConversationId));

    public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken)
    {
        var contacts = GetReader(sourceProfileId).ReadContacts();
        var results = new List<SourceParticipant>(contacts.Count);

        foreach (var contact in contacts.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new SourceParticipant
            {
                SourceUserId = contact.UserName,
                Remark = Normalize(contact.Remark),
                Nickname = Normalize(contact.NickName),
                Alias = Normalize(contact.Alias),
            });
        }

        return Task.FromResult<IReadOnlyList<SourceParticipant>>(results);
    }

    public async IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
        string sourceProfileId,
        string sourceConversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = GetReader(sourceProfileId);
        var contacts = reader.ReadContacts();
        var isGroup = sourceConversationId.EndsWith(WeChat4Schema.WeChatRoomSuffix, StringComparison.Ordinal);
        // A rotated conversation is read from several shards; sender resolution must use the
        // Name2Id map of the shard each row was actually read from (Issue #71).
        var name2IdByPartition = new Dictionary<string, IReadOnlyDictionary<long, string>>(StringComparer.Ordinal);
        foreach (var shard in reader.FindMessageShards(sourceConversationId))
        {
            name2IdByPartition.TryAdd(shard.Partition, shard.Name2Id);
        }

        var index = 0;
        foreach (var row in reader.ReadMessages(sourceConversationId, cancellationToken))
        {
            var (type, subType) = WeChat4Schema.Unpack(row.LocalType);

            WeChatContentDecoder.TryDecode(row.Content, row.ContentCompression, out var content, out var decodeFailure);

            var parsed = decodeFailure is not null
                ? new ParsedWeChatContent(
                    SourceMessageContent.Unparsed(null) with { IsPartial = true },
                    null,
                    null,
                    decodeFailure)
                : WeChatContentParser.Parse(
                    type,
                    subType,
                    content,
                    isGroup,
                    candidate => contacts.ContainsKey(candidate));

            name2IdByPartition.TryGetValue(row.Partition, out var name2Id);
            var senderSourceId = ResolveSender(
                row.RealSenderId,
                parsed.SenderHint,
                name2Id,
                contacts,
                sourceConversationId,
                isGroup);

            yield return new SourceMessage
            {
                SourceConversationId = sourceConversationId,
                SenderSourceUserId = senderSourceId,
                OccurredAt = WeChatAccountReader.ToLocalTime(row.CreateTime),
                SourceType = type.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceSubtype = subType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourcePartition = row.Partition,
                SourceMessageId = BuildSourceMessageId(row),
                SourceOrderKey = row.LocalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Content = parsed.Content,
                Reply = parsed.Reply,
            };

            // The reader is synchronous; yielding periodically keeps cancellation responsive
            // while the caller's operation runs on a background thread.
            if (++index % 512 == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Sender resolution order, most trustworthy first:
    /// the explicit <c>&lt;sender&gt;:</c> prefix that the client itself wrote for group
    /// messages, then the payload's own <c>fromusername</c>, then the shard's name table.
    /// A null result is preferred over a guess.
    /// </summary>
    private static string? ResolveSender(
        long realSenderId,
        string? senderHint,
        IReadOnlyDictionary<long, string>? name2Id,
        IReadOnlyDictionary<string, WeChatContactRow> contacts,
        string sourceConversationId,
        bool isGroup)
    {
        if (!string.IsNullOrWhiteSpace(senderHint) && contacts.ContainsKey(senderHint))
        {
            return senderHint;
        }

        if (name2Id is not null && name2Id.TryGetValue(realSenderId, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped)
            && (!isGroup || contacts.ContainsKey(mapped) || mapped == sourceConversationId))
        {
            return mapped;
        }

        // A direct conversation has exactly two possible senders; the peer is the only
        // candidate that is not the account itself, and the account is already known to
        // the archive. Falling back to the conversation id here is exact, not a guess.
        return isGroup ? null : sourceConversationId;
    }

    /// <summary>
    /// Upstream identity for a record. A server id is globally stable, so it is preferred;
    /// otherwise the shard plus local auto-increment id is a documented composite that is
    /// stable for a given source snapshot. docs/DATA_MODEL.md section 16.
    /// </summary>
    private static string BuildSourceMessageId(WeChatMessageRow row) =>
        row.ServerId > 0
            ? $"s:{row.ServerId}"
            : $"l:{row.Partition}:{row.LocalId}";

    /// <summary>
    /// Referenced messages are addressed by server id, matching <see cref="BuildSourceMessageId"/>.
    /// </summary>
    public static string BuildReplySourceMessageId(string serverId) => $"s:{serverId}";

    private static string? ResolveTitle(WeChatContactRow? contact, string userName)
    {
        if (contact is not null)
        {
            var named = Normalize(contact.Remark) ?? Normalize(contact.NickName);
            if (named is not null)
            {
                return named;
            }
        }

        return userName;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private WeChatAccountReader GetReader(string sourceProfileId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readers.TryGetValue(sourceProfileId, out var existing))
            {
                return existing;
            }

            if (_capturedDescriptor is not null)
                throw new InvalidOperationException("The captured generation does not contain the requested source profile.");

            var account = Discover()
                .SelectMany(i => i.Accounts)
                .FirstOrDefault(a => string.Equals(a.SourceProfileId, sourceProfileId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"No local WeChat account '{sourceProfileId}' was found.");

            var reader = new WeChatAccountReader(account, GetCache());
            _readers[sourceProfileId] = reader;
            return reader;
        }
    }

    private SqlCipherDatabaseCache GetCache()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (_keys is null)
        {
            var databases = Discover()
                .SelectMany(i => i.Accounts)
                .SelectMany(WeChatDataLocator.EnumerateDatabases)
                .ToList();

            var result = _keyAcquirer.Acquire(databases);
            if (!result.Succeeded || result.KeySet is null)
            {
                _keyFailure = result.Message;
                throw new WeChatKeyUnavailableException(result.Message);
            }

            _keys = result.KeySet;
        }

        _cache = new SqlCipherDatabaseCache(_keys);
        return _cache;
    }

    private IReadOnlyList<WeChatInstallation> Discover() =>
        _installations ??= WeChatDataLocator.Discover();

    /// <summary>Last key-acquisition failure, for display in the UI.</summary>
    public string? LastKeyFailure
    {
        get
        {
            lock (_gate)
            {
                return _keyFailure;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _readers.Clear();
            _cache?.Dispose();
            _cache = null;
        }
    }
}
