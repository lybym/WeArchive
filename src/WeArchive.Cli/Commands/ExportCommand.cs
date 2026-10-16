using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Export;
using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive export (--conversation &lt;id-or-alias&gt; | --collection &lt;name&gt;)
/// [--since &lt;time&gt;] [--until &lt;time&gt;] [--output &lt;dir&gt;]</c>: publishes the selected
/// conversations' JSONL dataset (docs/PRD.md FR-18/FR-23, docs/EXPORT_PRD.md section 7, issue #106).
/// <para>
/// This is a thin transport adapter over the canonical-archive-only export boundary (Issue #66).
/// A conversation selector resolves from the canonical archive through
/// <see cref="ArchiveConversationResolver"/>; a Collection resolves through the one authoritative
/// Collection catalog inside <see cref="ArchiveWorkflow"/>. The whole
/// <c>Canonical SQLite -&gt; JSONL/YAML/JSON</c> derivation is delegated to the workflow. No live
/// WeChat/source-adapter access, capture, Raw Vault ingest, implicit import or canonical mutation
/// is reachable from this command: refreshing canonical state is <c>sync</c>'s job, and the command
/// only parses options, renders the result and maps failures to the documented error codes. The
/// workflow — not this command — owns the R1 in-process export staging/backup/restore behaviour.
/// </para>
/// </summary>
public sealed class ExportCommand : ICliCommand
{
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "--conversation",
        "--collection",
        "--since",
        "--until",
        "--output",
        "-o",
    };

    private readonly ArchiveConversationResolver _resolver;
    private readonly ArchiveWorkflow _workflow;
    private readonly CliExportDefaults _defaults;
    private readonly IClock _clock;

    public ExportCommand(
        ArchiveConversationResolver resolver,
        ArchiveWorkflow workflow,
        CliExportDefaults defaults,
        IClock clock)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Name => "export";

    public string Description => "Export a conversation or Collection to a JSONL dataset.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var (options, positional, parseError) = CommandOptionParser.Parse(args, KnownOptions);
        if (parseError is not null)
        {
            context.WriteError(CliErrorCode.UsageError, parseError);
            return ExitCode.UsageError;
        }

        if (positional.Count > 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{positional[0]}' for export.");
            return ExitCode.UsageError;
        }

        options.TryGetValue("--conversation", out var conversation);
        options.TryGetValue("--collection", out var collection);

        // Exactly one scope, exactly as the query commands scope theirs: one export answers one
        // resolved scope, never an ambiguous union (docs/CLI.md `export`).
        if (conversation is not null && collection is not null)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "export accepts either --conversation <id-or-alias> or --collection <name>, not both.");
            return ExitCode.UsageError;
        }

        if (conversation is null && collection is null)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "export requires --conversation <id-or-alias> or --collection <name>.");
            return ExitCode.UsageError;
        }

        var since = options.GetValueOrDefault("--since");
        var until = options.GetValueOrDefault("--until");
        if (!TryParseBounds(context, since, until, out var sinceInstant, out var untilInstant))
        {
            return ExitCode.UsageError;
        }

        // --output is a single-invocation package root: the exporter rebuilds manifest.json,
        // conversations.yaml and identities.yaml from this invocation's selection only, so a
        // shared root would de-index a previously exported package. When the caller does not name
        // a root, each scope gets its own self-consistent default root so exporting one scope can
        // never damage another's output (docs/EXPORT_PRD.md section 3.2, docs/CLI.md `export`).
        if (!TryParseExplicitOutput(context, options, out var explicitOutput))
        {
            return ExitCode.UsageError;
        }

        if (collection is not null)
        {
            var collectionOutput = explicitOutput ?? DefaultCollectionRoot(context, collection);
            if (collectionOutput is null)
            {
                return ExitCode.UsageError;
            }

            return await ExportCollectionAsync(context, collection, collectionOutput, sinceInstant, untilInstant, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ExportConversationAsync(context, conversation!, explicitOutput, sinceInstant, untilInstant, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> ExportConversationAsync(
        CliContext context,
        string conversation,
        string? explicitOutput,
        DateTimeOffset? since,
        DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        CliReporting.Progress(context, $"Resolving conversation '{conversation}' in the canonical archive…");
        var resolved = await _resolver.ResolveAsync(conversation, cancellationToken).ConfigureAwait(false);
        switch (resolved.Status)
        {
            case ArchiveConversationResolutionStatus.NotFound:
                context.WriteError(
                    CliErrorCode.ConversationNotFound,
                    $"conversation '{conversation}' was not found in the canonical archive. " +
                    "Sync it before exporting.");
                return ExitCode.Failure;

            case ArchiveConversationResolutionStatus.Ambiguous:
                // An archived upstream source id that exists under more than one account is
                // ambiguous: the caller must use the canonical stable id. Reporting the ordered
                // candidate list keeps the failure deterministic and never falls back to the live
                // source or an arbitrary account (Issue #66, Gap E).
                context.WriteError(
                    CliErrorCode.ConversationAmbiguous,
                    $"conversation selector '{conversation}' matches {resolved.Candidates.Count} archived " +
                    $"conversations ({string.Join(", ", resolved.Candidates.Select(c => c.Id))}); " +
                    "use the canonical stable conversation id.");
                return ExitCode.Failure;
        }

        var archived = resolved.Conversation!;

        // The default destination depends on the resolved conversation's stable id, so it is
        // computed here rather than during option parsing.
        var output = explicitOutput ?? Path.Combine(_defaults.DefaultOutputDirectory, archived.Id);

        var request = new ExportConversationRequest
        {
            ConversationId = archived.Id,
            OutputDirectory = output,
            Since = since,
            Until = until,
        };

        return await ExportAsync(context, request, output, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExportCollectionAsync(
        CliContext context,
        string collection,
        string output,
        DateTimeOffset? since,
        DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        var request = new ExportCollectionRequest
        {
            CollectionName = collection,
            OutputDirectory = output,
            Since = since,
            Until = until,
        };

        CliReporting.Progress(context, $"Resolving collection '{collection}' through the Collection catalog…");

        ExportResult result;
        try
        {
            result = await _workflow
                .ExportCollectionAsync(request, new CliProgress<OperationProgress>(context, FormatProgress), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CollectionNotFoundException exception)
        {
            // An unknown name is the same deterministic outcome the collection, sync and query
            // commands report: the authoritative configuration does not define it.
            context.WriteError(CliErrorCode.CollectionNotFound, exception.Message);
            return ExitCode.Failure;
        }
        catch (CollectionConfigurationException exception)
        {
            // Invalid configuration is a usage/configuration validation failure (exit 2), exactly
            // as every other Collection-scoped command maps it. The file is diagnosed, never
            // rewritten.
            context.WriteError(CliErrorCode.CollectionConfigInvalid, exception.Message);
            return ExitCode.UsageError;
        }

        return WriteSuccess(context, result);
    }

    private async Task<int> ExportAsync(
        CliContext context,
        ExportConversationRequest request,
        string output,
        CancellationToken cancellationToken)
    {
        CliReporting.Progress(context, $"Exporting '{request.ConversationId}' to {output}…");
        var progress = new CliProgress<OperationProgress>(context, FormatProgress);
        var result = await _workflow
            .ExportConversationAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);

        return WriteSuccess(context, result);
    }

    private int WriteSuccess(CliContext context, ExportResult result)
    {
        // A Core exporter contract allows Succeeded=false without throwing. That must never be
        // reported as a successful process: surface the documented failure document (exit 1)
        // instead of a result document that claims a package was published.
        if (!result.Succeeded)
        {
            context.WriteError(
                CliErrorCode.Failure,
                result.FailureReason ?? "The export did not complete; no package was published.");
            return ExitCode.Failure;
        }

        var dto = new ExportResultDto
        {
            Succeeded = result.Succeeded,
            OutputDirectory = result.OutputDirectory,
            Scope = result.Scope is null ? null : ExportScopeDto.From(result.Scope),
            ConversationIds = [.. result.ConversationIds],
            RecordCount = result.RecordCount,
            UnknownCount = result.UnknownCount,
            PartialCount = result.PartialCount,
            TimeRange = new ExportTimeRangeDto
            {
                FirstMessageAt = FormatTimestamp(result.FirstMessageAt),
                LastMessageAt = FormatTimestamp(result.LastMessageAt),
            },
            Files = [.. result.Files.Select(f => new ExportFileDto
            {
                Path = f.RelativePath,
                Kind = f.Kind,
                RecordCount = f.RecordCount,
            })],
            ConversationPaths = [.. result.ConversationPaths],
            Diagnostics = [.. result.Diagnostics.Select(CliDiagnosticDto.From)],
        };

        WriteResult(context, dto);
        return ExitCode.Success;
    }

    /// <summary>
    /// Parses the optional time bounds in the documented <c>message list</c> grammar. Both bounds
    /// are inclusive instants; a value without an explicit offset is interpreted in the machine's
    /// local offset, and an unparseable value is a usage failure rather than a silently widened
    /// range (docs/CLI.md <c>message list</c> time values).
    /// </summary>
    private bool TryParseBounds(
        CliContext context,
        string? since,
        string? until,
        out DateTimeOffset? sinceInstant,
        out DateTimeOffset? untilInstant)
    {
        sinceInstant = null;
        untilInstant = null;

        if (since is not null)
        {
            if (!FilterValues.TryParseInstant(since, _clock.LocalOffset, out var parsed, out var error))
            {
                context.WriteError(CliErrorCode.UsageError, $"--since: {error}");
                return false;
            }

            sinceInstant = parsed;
        }

        if (until is not null)
        {
            if (!FilterValues.TryParseInstant(until, _clock.LocalOffset, out var parsed, out var error))
            {
                context.WriteError(CliErrorCode.UsageError, $"--until: {error}");
                return false;
            }

            untilInstant = parsed;
        }

        if (sinceInstant is not null && untilInstant is not null && sinceInstant > untilInstant)
        {
            context.WriteError(CliErrorCode.UsageError, "--since must not be later than --until.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses the explicit <c>--output</c>/<c>-o</c> value. Returns false after reporting a usage
    /// failure; <paramref name="explicitOutput"/> is null when the option is omitted and the
    /// trimmed value otherwise.
    /// </summary>
    private static bool TryParseExplicitOutput(
        CliContext context,
        IReadOnlyDictionary<string, string> options,
        out string? explicitOutput)
    {
        if (!options.TryGetValue("--output", out var output) && !options.TryGetValue("-o", out output))
        {
            explicitOutput = null;
            return true;
        }

        output = output.Trim();
        if (output.Length == 0)
        {
            context.WriteError(CliErrorCode.UsageError, "--output requires a value.");
            explicitOutput = null;
            return false;
        }

        explicitOutput = output;
        return true;
    }

    /// <summary>
    /// The default Collection-scope package root: a distinct <c>collection-&lt;name&gt;</c> root
    /// under the host exports directory, which can never collide with a stable-id conversation
    /// root because stable ids are <c>g_…</c>/<c>u_…</c>. A Collection name that cannot form a
    /// directory name returns null after reporting the usage failure; the caller can still export
    /// it under an explicit <c>--output</c>.
    /// </summary>
    private string? DefaultCollectionRoot(CliContext context, string collection)
    {
        if (collection.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"collection name '{collection}' cannot be used as a default output directory name; " +
                "pass --output <dir> explicitly.");
            return null;
        }

        return Path.Combine(_defaults.DefaultOutputDirectory, $"collection-{collection}");
    }

    private static void WriteResult(CliContext context, ExportResultDto result)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(result));
            return;
        }

        context.Stdout.WriteLine($"Exported {result.RecordCount} record(s) to {result.OutputDirectory}");
        if (result.Scope is not null)
        {
            context.Stdout.WriteLine($"  scope:          {DescribeScope(result.Scope)}");
        }

        context.Stdout.WriteLine($"  conversations: {string.Join(", ", result.ConversationIds)}");
        context.Stdout.WriteLine($"  output:         {Path.Combine(result.OutputDirectory, "manifest.json")}");
        context.Stdout.WriteLine(
            $"  coverage:       {result.UnknownCount} unknown, {result.PartialCount} partial");

        if (result.Diagnostics.Count > 0)
        {
            context.Stdout.WriteLine("  diagnostics:");
            foreach (var d in result.Diagnostics)
            {
                context.Stdout.WriteLine($"    {d.Severity} {d.Code}: {d.Message}");
            }
        }
    }

    /// <summary>
    /// One deterministic human scope description; the unarchived members are listed so a canonical
    /// archive behind its configuration is visible in human output too.
    /// </summary>
    private static string DescribeScope(ExportScopeDto scope) => scope.Kind switch
    {
        "collection" => string.Concat(
            $"collection {scope.Collection} ",
            $"({scope.ArchivedMemberConversationCount}/{scope.MemberConversationCount} members archived",
            scope.UnarchivedConversationIds is { Count: > 0 }
                ? $", unarchived: {string.Join(", ", scope.UnarchivedConversationIds)}"
                : string.Empty,
            scope.InvalidMemberCount > 0 ? $", {scope.InvalidMemberCount} invalid" : string.Empty,
            scope.DuplicateMemberCount > 0 ? $", {scope.DuplicateMemberCount} duplicate" : string.Empty,
            DescribeBounds(scope),
            ")"),
        _ => string.Concat($"conversation {scope.ConversationId}", DescribeBounds(scope)),
    };

    private static string DescribeBounds(ExportScopeDto scope) =>
        scope.Since is null && scope.Until is null
            ? string.Empty
            : $", {scope.Since ?? "…"} … {scope.Until ?? "…"}";

    private static string FormatProgress(OperationProgress p) =>
        p.Total > 0
            ? $"{p.Stage}: {p.Processed}/{p.Total}"
            : string.IsNullOrEmpty(p.Stage) ? string.Empty : p.Stage;

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value is null ? null : value.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}
