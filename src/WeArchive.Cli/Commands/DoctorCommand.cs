using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive doctor</c>: reports platform, source and archive readiness without
/// changing source data (docs/PRD.md FR-02).
/// <para>
/// This is a thin adapter: it calls <see cref="ISourceAdapter.DescribeSourceAsync"/> and
/// <see cref="IArchiveStore"/> readiness checks and renders the result. It contains no
/// WeChat schema logic, no archive publication semantics and no export business rules.
/// </para>
/// </summary>
public sealed class DoctorCommand : ICliCommand
{
    private readonly ISourceAdapter _adapter;
    private readonly IArchiveStore _archive;

    public DoctorCommand(ISourceAdapter adapter, IArchiveStore archive)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    }

    public string Name => "doctor";

    public string Description => "Report source and archive readiness.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        context.ReportProgress("Checking source…");
        var source = await CheckSourceAsync(cancellationToken).ConfigureAwait(false);

        context.ReportProgress("Checking archive…");
        var archive = await CheckArchiveAsync(cancellationToken).ConfigureAwait(false);

        var result = new DoctorResultDto
        {
            Ready = source.Available && archive.Available,
            Source = source,
            Archive = archive,
        };

        WriteResult(context, result);
        return ExitCode.Success;
    }

    private async Task<SourceCheckDto> CheckSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = await _adapter
                .DescribeSourceAsync(cancellationToken)
                .ConfigureAwait(false);

            return new SourceCheckDto
            {
                Available = descriptor.IsAvailable,
                Adapter = descriptor.AdapterName,
                AdapterVersion = descriptor.AdapterVersion,
                SourceVersion = descriptor.SourceVersion,
                SourceProduct = descriptor.SourceProductName,
                UnavailableReason = descriptor.UnavailableReason,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // doctor is a health check: it must not crash on an unavailable source.
            return new SourceCheckDto
            {
                Available = false,
                Adapter = _adapter.AdapterName,
                AdapterVersion = _adapter.AdapterVersion,
                UnavailableReason = ex.Message,
            };
        }
    }

    private async Task<ArchiveCheckDto> CheckArchiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var stats = await _archive
                .GetArchiveStatsAsync(cancellationToken)
                .ConfigureAwait(false);

            return new ArchiveCheckDto
            {
                Available = true,
                Path = stats.ArchivePath,
                AccountCount = stats.AccountCount,
                ConversationCount = stats.ConversationCount,
                ParticipantCount = stats.ParticipantCount,
                MessageCount = stats.MessageCount,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ArchiveCheckDto
            {
                Available = false,
                Path = _archive.ArchivePath,
                UnavailableReason = ex.Message,
            };
        }
    }

    private static void WriteResult(CliContext context, DoctorResultDto result)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(result));
            return;
        }

        var status = result.Ready ? "ready" : "not ready";
        context.Stdout.WriteLine($"WeArchive: {status}");

        context.Stdout.WriteLine($"  source:  {SourceStatus(result.Source)}");
        if (!string.IsNullOrEmpty(result.Source.UnavailableReason))
            context.Stdout.WriteLine($"           {result.Source.UnavailableReason}");

        context.Stdout.WriteLine($"  archive: {ArchiveStatus(result.Archive)}");
        if (!string.IsNullOrEmpty(result.Archive.UnavailableReason))
            context.Stdout.WriteLine($"           {result.Archive.UnavailableReason}");
    }

    private static string SourceStatus(SourceCheckDto source)
    {
        if (!source.Available)
            return "unavailable";
        var parts = new List<string> { source.Adapter };
        if (source.SourceVersion is { Length: > 0 } v)
            parts.Add($"v{v}");
        return string.Join(" ", parts);
    }

    private static string ArchiveStatus(ArchiveCheckDto archive)
    {
        if (!archive.Available)
            return "unavailable";
        return $"{archive.MessageCount} message(s), {archive.ConversationCount} conversation(s)";
    }
}
