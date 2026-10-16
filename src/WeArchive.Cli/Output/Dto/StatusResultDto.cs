using System.Text.Json.Serialization;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.RawVault;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// <c>wearchive status --json</c> result (issue #107, docs/CLI.md, docs/HARNESS.md section 10).
/// Field names are pinned by <c>[JsonPropertyName]</c> so a C# rename can never change the machine
/// contract (docs/PRD.md FR-22).
/// <para>
/// One read-only report of the three independent freshness stages — capture (latest published
/// preservation generation), ingest (committed canonical publication progress) and canonical (the
/// queryable archive state). The stages are reported exactly as
/// <see cref="ArchiveQueryService.GetFreshnessAsync"/> observes them and are never inferred from one
/// another: a captured-but-not-ingested account stays visible as such, and a stage that cannot be
/// read degrades to an explicit null plus a reason instead of failing the report or presenting as
/// fresh. The report is a statement about observed archive state, never a completeness claim about
/// the live source.
/// </para>
/// </summary>
public sealed record StatusResultDto
{
    [JsonPropertyName("basis")]
    public required StatusBasisDto Basis { get; init; }

    [JsonPropertyName("canonical")]
    public required StatusCanonicalDto Canonical { get; init; }

    [JsonPropertyName("capture")]
    public required StatusCaptureDto Capture { get; init; }

    [JsonPropertyName("ingest")]
    public required StatusIngestDto Ingest { get; init; }

    public static StatusResultDto From(ArchiveFreshness freshness)
    {
        ArgumentNullException.ThrowIfNull(freshness);

        return new StatusResultDto
        {
            Basis = new StatusBasisDto
            {
                Source = "canonical_archive",
                ArchivePath = freshness.Canonical.ArchivePath,
                QueriedAt = freshness.Canonical.QueriedAt,
            },
            Canonical = new StatusCanonicalDto
            {
                AccountCount = freshness.Canonical.AccountCount,
                ConversationCount = freshness.Canonical.ConversationCount,
                ParticipantCount = freshness.Canonical.ParticipantCount,
                MessageCount = freshness.Canonical.MessageCount,
                LastMessageAt = freshness.Canonical.LastMessageAt,
            },
            Capture = new StatusCaptureDto
            {
                UnavailableReason = freshness.CaptureUnavailableReason,
                Accounts = [.. freshness.Capture.Select(StatusCaptureAccountDto.From)],
            },
            Ingest = new StatusIngestDto
            {
                Accounts = [.. freshness.Ingest.Select(StatusIngestAccountDto.From)],
            },
        };
    }
}

/// <summary>When and against which archive the status was read. Every stage below is "as of"
/// <c>queried_at</c>; the stages were each read independently at that moment.</summary>
public sealed record StatusBasisDto
{
    /// <summary>Always <c>canonical_archive</c>; status reads the archive and, through the
    /// source-neutral preservation contract, the capture stage — never a live source.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("archive_path")]
    public required string ArchivePath { get; init; }

    [JsonPropertyName("queried_at")]
    public required DateTimeOffset QueriedAt { get; init; }
}

/// <summary>The canonical archive's own queryable state (docs/DATA_MODEL.md section 23.3). These
/// counts are observations about the archive, never a claim that the source has been fully captured
/// or ingested.</summary>
public sealed record StatusCanonicalDto
{
    [JsonPropertyName("account_count")]
    public required int AccountCount { get; init; }

    [JsonPropertyName("conversation_count")]
    public required int ConversationCount { get; init; }

    [JsonPropertyName("participant_count")]
    public required int ParticipantCount { get; init; }

    [JsonPropertyName("message_count")]
    public required int MessageCount { get; init; }

    /// <summary>Instant of the newest archived message, or null for an empty archive.</summary>
    [JsonPropertyName("last_message_at")]
    public DateTimeOffset? LastMessageAt { get; init; }
}

/// <summary>The capture stage: the latest published preservation generation per known account,
/// through the source-neutral preservation contract. No vault path, partition fingerprint, artifact
/// checksum or artifact content crosses this surface.</summary>
public sealed record StatusCaptureDto
{
    /// <summary>
    /// Reason the preservation store could not be enumerated at all, or null when it could. When
    /// set, <c>accounts</c> lists only the accounts the canonical archive knows, so a vault-only
    /// account is visibly unknown rather than silently absent.
    /// </summary>
    [JsonPropertyName("unavailable_reason")]
    public string? UnavailableReason { get; init; }

    [JsonPropertyName("accounts")]
    public required IReadOnlyList<StatusCaptureAccountDto> Accounts { get; init; }
}

/// <summary>One account's latest published capture generation, or the explicit reason none can be
/// reported. A null <c>generation_id</c> always means "no generation can be reported" — none exists
/// yet, or this account's state could not be read — never "a generation was found and matched".</summary>
public sealed record StatusCaptureAccountDto
{
    [JsonPropertyName("account_id")]
    public required string AccountId { get; init; }

    /// <summary>Generation ID of the latest valid published generation, or null when none can be reported.</summary>
    [JsonPropertyName("generation_id")]
    public string? GenerationId { get; init; }

    /// <summary>When that generation was captured, or null with a null generation.</summary>
    [JsonPropertyName("capture_time")]
    public DateTimeOffset? CaptureTime { get; init; }

    /// <summary>The generation's completeness verdict as a stable wire name
    /// (<c>complete</c>, <c>partial</c>, <c>incomplete</c>), or null with a null generation.</summary>
    [JsonPropertyName("completeness")]
    public string? Completeness { get; init; }

    [JsonPropertyName("artifact_count")]
    public int ArtifactCount { get; init; }

    /// <summary>The generation's predecessor in the account's append-only chain, or null for the first.</summary>
    [JsonPropertyName("previous_generation_id")]
    public string? PreviousGenerationId { get; init; }

    /// <summary>Short engineering reason this account's capture state could not be read, or null when
    /// it was read. It is a diagnostic a caller reports, never an input a caller parses.</summary>
    [JsonPropertyName("unavailable_reason")]
    public string? UnavailableReason { get; init; }

    public static StatusCaptureAccountDto From(CaptureFreshness capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        return new StatusCaptureAccountDto
        {
            AccountId = capture.AccountId,
            GenerationId = capture.GenerationId,
            CaptureTime = capture.CaptureTime,
            Completeness = capture.Completeness?.ToString().ToLowerInvariant(),
            ArtifactCount = capture.ArtifactCount,
            PreviousGenerationId = capture.PreviousGenerationId,
            UnavailableReason = capture.UnavailableReason,
        };
    }
}

/// <summary>The ingest stage: committed canonical publication progress per account, projected by the
/// ingest-progress owner behind the Core contract. No checkpoint payload, cursor encoding or scope
/// vocabulary crosses this surface.</summary>
public sealed record StatusIngestDto
{
    /// <summary>One row per account the canonical archive knows — plus any account that has capture
    /// state, so a captured-but-not-ingested account is visible as such instead of absent.</summary>
    [JsonPropertyName("accounts")]
    public required IReadOnlyList<StatusIngestAccountDto> Accounts { get; init; }
}

/// <summary>One account's committed ingest progress. Every null field is an explicit stage fact
/// ("nothing ingested yet"), never a silent omission.</summary>
public sealed record StatusIngestAccountDto
{
    [JsonPropertyName("account_id")]
    public required string AccountId { get; init; }

    /// <summary>When the most recent conversation-scope canonical publication committed, or null when
    /// nothing was ingested yet.</summary>
    [JsonPropertyName("last_ingest_at")]
    public DateTimeOffset? LastIngestAt { get; init; }

    /// <summary>Generation of the most recently committed conversation ingest, or null. An older
    /// generation id here while capture reports a newer one is exactly how "canonical is behind the
    /// preservation store" is visible.</summary>
    [JsonPropertyName("latest_ingested_generation_id")]
    public string? LatestIngestedGenerationId { get; init; }

    /// <summary>The stable conversation ID <see cref="LatestIngestedGenerationId"/> belongs to, or null.</summary>
    [JsonPropertyName("latest_ingested_conversation_id")]
    public string? LatestIngestedConversationId { get; init; }

    /// <summary>When a complete account-wide generation scan last finished, or null. Scoped ingest
    /// never advances it.</summary>
    [JsonPropertyName("last_account_scan_at")]
    public DateTimeOffset? LastAccountScanAt { get; init; }

    public static StatusIngestAccountDto From(IngestFreshness ingest)
    {
        ArgumentNullException.ThrowIfNull(ingest);

        return new StatusIngestAccountDto
        {
            AccountId = ingest.AccountId,
            LastIngestAt = ingest.LastIngestAt,
            LatestIngestedGenerationId = ingest.LatestIngestedGenerationId,
            LatestIngestedConversationId = ingest.LatestIngestedConversationId,
            LastAccountScanAt = ingest.LastAccountScanAt,
        };
    }
}
