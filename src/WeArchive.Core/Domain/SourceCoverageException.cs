namespace WeArchive.Core.Domain;

/// <summary>
/// A typed signal that a source adapter could not provide complete coverage for the
/// requested conversation — for example a missing or unreadable message shard. The
/// adapter throws this instead of silently yielding an empty stream so the importer can
/// surface a <c>Fatal</c> diagnostic (FR-14) rather than reporting a successful empty
/// import. docs/PRD.md FR-14, docs/ARCHITECTURE.md section 11.
/// <para>
/// This is a generic, source-agnostic exception: the WeChat-specific reasons a shard is
/// unreadable stay behind the adapter boundary, and only the stable diagnostic
/// <see cref="Code"/> and a short engineering message cross it.
/// </para>
/// </summary>
public sealed class SourceCoverageException : Exception
{
    public string Code { get; }

    public SourceCoverageException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }
}
