using WeArchive.Core.Domain;

namespace WeArchive.Core.Abstractions;

/// <summary>
/// Source-neutral access to committed ingest progress, for freshness reporting.
/// docs/HARNESS.md section 10, docs/DATA_MODEL.md section 23.3.
/// <para>
/// The abstraction exists so that the *owner* of the ingest cursor interprets it. The checkpoint
/// payload and the scope vocabulary are implementation details of the Raw Vault ingest component;
/// the canonical archive store only enumerates the rows it persists. A caller therefore receives a
/// stable <see cref="IngestFreshness"/> projection and never a payload, an encoding or a table
/// shape.
/// </para>
/// </summary>
public interface IIngestProgressSource
{
    /// <summary>
    /// Reports the latest committed conversation-scope canonical publication and the last completed
    /// account-wide generation scan per account. Accounts with no committed ingest progress are
    /// simply absent; the caller decides how to present that.
    /// </summary>
    Task<IReadOnlyList<IngestFreshness>> GetIngestFreshnessAsync(CancellationToken cancellationToken);
}