using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Query;

namespace WeArchive.Cli.Commands;

/// <summary>
/// Shared plumbing of the query commands (<c>message list</c>, <c>context</c>): mapping a
/// <see cref="ArchiveQueryException"/> onto the documented CLI process contract, parsing the
/// documented time/limit value forms, and rendering canonical messages.
/// <para>
/// The mapping lives here so the two commands cannot disagree about whether a failure was the
/// caller's input (exit 2) or the archive/identifier state (exit 1).
/// </para>
/// </summary>
public static class QueryCommandSupport
{
    /// <summary>
    /// Reports a query failure and returns the process exit code it maps to. A validation failure
    /// is a usage/configuration failure (exit 2); a missing object or an unreadable archive is a
    /// runtime failure (exit 1).
    /// </summary>
    public static int ReportQueryFailure(CliContext context, ArchiveQueryException exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.Kind == ArchiveQueryFailureKind.Validation)
        {
            // `invalid_request` is the generic validation code documented as `usage_error`; a
            // specific validation code such as `cursor_invalid` keeps its own name.
            var code = exception.Code == ArchiveQueryErrorCodes.InvalidRequest
                ? CliErrorCode.UsageError
                : exception.Code;
            context.WriteError(code, exception.Message);
            return ExitCode.UsageError;
        }

        context.WriteError(exception.Code, exception.Message);
        return ExitCode.Failure;
    }

    /// <summary>
    /// Parses a whole-number option. Range validation belongs to the query service, so the CLI only
    /// decides whether the caller wrote a number at all.
    /// </summary>
    public static bool TryParseCount(
        string text,
        string option,
        out int value,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return true;

        error = $"option {option} requires a whole number.";
        return false;
    }

    /// <summary>
    /// Renders one canonical message as a single human-readable line. The stable message ID leads,
    /// because it is what a human feeds back into <c>context</c>.
    /// </summary>
    public static string Line(MessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var sender = message.SenderId ?? "(unresolved)";
        return $"{message.Id}  {FormatTimestamp(message.OccurredAt)}  {sender}  {message.Type}  {Flatten(message.Text)}";
    }

    /// <summary>
    /// Canonical timestamps with the source offset. UTC is the render form because it is
    /// unambiguous without repeating the archive's original offset for every line.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);

    /// <summary>
    /// Collapses line breaks and tabs so one canonical message always occupies exactly one human
    /// output line; the semantic text itself is unchanged.
    /// </summary>
    private static string Flatten(string text) =>
        text.ReplaceLineEndings(" ").Replace('\t', ' ');
}