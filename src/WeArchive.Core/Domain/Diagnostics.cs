namespace WeArchive.Core.Domain;

/// <summary>
/// Error/diagnostic model. docs/ARCHITECTURE.md section 11.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>No correctness impact.</summary>
    Info,

    /// <summary>The operation can continue but completeness is uncertain.</summary>
    Partial,

    /// <summary>Import/export cannot safely continue.</summary>
    Fatal,
}

public static class DiagnosticCodes
{
    public const string SourceUnavailable = "source_unavailable";
    public const string SourceDiscovered = "source_discovered";
    public const string SourceNotRunning = "source_not_running";
    public const string KeyAcquisitionFailed = "key_acquisition_failed";
    public const string PartitionUnreadable = "partition_unreadable";
    public const string PartitionMissing = "partition_missing";
    public const string SourceMessageIdUnavailable = "source_message_id_unavailable";
    public const string DatabaseIntegrityWarning = "database_integrity_warning";
    public const string WalFramesRejected = "wal_frames_rejected";
    public const string UnknownMessageType = "unknown_message_type";
    public const string PartialAppMessage = "partial_app_message";
    public const string UnresolvedReplyTarget = "unresolved_reply_target";
    public const string ReplySnapshotOnly = "reply_snapshot_only";
    public const string LinkWrapperUrlOnly = "link_wrapper_url_only";
    public const string LinkMetadataMissing = "link_metadata_missing";
    public const string FileNameUnavailable = "file_name_unavailable";
    public const string VoiceDurationUnavailable = "voice_duration_unavailable";
    public const string SenderUnresolved = "sender_unresolved";
    public const string NoNewRecords = "no_new_records";
    public const string ContentDecompressionFailed = "content_decompression_failed";
    public const string UnsupportedClientVersion = "unsupported_client_version";
}

/// <summary>
/// A structured diagnostic. Diagnostics never carry chat content: only identifiers,
/// upstream type codes and short engineering explanations.
/// </summary>
public sealed record ImportDiagnostic
{
    public required DiagnosticSeverity Severity { get; init; }

    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? ConversationId { get; init; }

    public string? MessageId { get; init; }

    public string? SourceType { get; init; }

    public string? SourceSubtype { get; init; }

    /// <summary>How many records this diagnostic aggregates, when it is a rollup.</summary>
    public int Count { get; init; } = 1;

    public static ImportDiagnostic Info(string code, string message) =>
        new() { Severity = DiagnosticSeverity.Info, Code = code, Message = message };

    public static ImportDiagnostic Partial(string code, string message, string? sourceType = null, string? sourceSubtype = null) =>
        new()
        {
            Severity = DiagnosticSeverity.Partial,
            Code = code,
            Message = message,
            SourceType = sourceType,
            SourceSubtype = sourceSubtype,
        };

    public static ImportDiagnostic Fatal(string code, string message) =>
        new() { Severity = DiagnosticSeverity.Fatal, Code = code, Message = message };
}

/// <summary>Aggregated diagnostics for one operation.</summary>
public sealed class DiagnosticBag
{
    private readonly List<ImportDiagnostic> _items = [];

    public IReadOnlyList<ImportDiagnostic> Items => _items;

    public int FatalCount => _items.Count(d => d.Severity == DiagnosticSeverity.Fatal);

    public int PartialCount => _items.Count(d => d.Severity == DiagnosticSeverity.Partial);

    public int InfoCount => _items.Count(d => d.Severity == DiagnosticSeverity.Info);

    public void Add(ImportDiagnostic diagnostic) => _items.Add(diagnostic);

    public void AddRange(IEnumerable<ImportDiagnostic> diagnostics) => _items.AddRange(diagnostics);

    public void Info(string code, string message) => Add(ImportDiagnostic.Info(code, message));

    public void Partial(string code, string message, string? sourceType = null, string? sourceSubtype = null) =>
        Add(ImportDiagnostic.Partial(code, message, sourceType, sourceSubtype));

    public void Fatal(string code, string message) => Add(ImportDiagnostic.Fatal(code, message));

    /// <summary>
    /// Collapses repeated per-message diagnostics into counted rollups so that a
    /// conversation with thousands of unparsed records does not produce thousands
    /// of UI rows. The rollups keep the upstream type codes.
    /// </summary>
    public IReadOnlyList<ImportDiagnostic> Rollup()
    {
        var passthrough = new List<ImportDiagnostic>();
        var groups = new Dictionary<(string Code, string? Type, string? SubType, DiagnosticSeverity Severity), (ImportDiagnostic First, int Count)>();

        foreach (var d in _items)
        {
            if (d.ConversationId is not null || d.MessageId is not null)
            {
                passthrough.Add(d);
                continue;
            }

            var key = (d.Code, d.SourceType, d.SourceSubtype, d.Severity);
            if (groups.TryGetValue(key, out var existing))
            {
                groups[key] = (existing.First, existing.Count + 1);
            }
            else
            {
                groups[key] = (d, 1);
            }
        }

        var result = new List<ImportDiagnostic>(passthrough);
        foreach (var (_, value) in groups.OrderBy(g => g.Key.Code, StringComparer.Ordinal))
        {
            result.Add(value.First with { Count = value.Count });
        }

        return result;
    }
}
