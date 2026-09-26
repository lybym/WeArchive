namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Option parsing for command families whose subcommands take several value options
/// (<c>message list</c>, <c>context</c>). It exists so those commands validate their own grammar
/// once instead of re-implementing the same token walk.
/// <para>
/// It is deliberately scoped to those commands: the pre-existing commands keep their own
/// hand-written parsing, and this is not a refactor of them. Global options
/// (<c>--json</c>, <c>--quiet</c>, <c>--no-input</c>) are removed by
/// <see cref="CommandLineParser"/> before a command sees its arguments.
/// </para>
/// </summary>
public static class CommandOptionParser
{
    /// <summary>
    /// Walks <paramref name="args"/>, accepting <c>--name value</c> and <c>--name=value</c> for the
    /// names in <paramref name="knownOptions"/>, and collecting everything else as positional.
    /// A bare <c>--</c> separator ends option parsing so an identifier that begins with <c>-</c>
    /// remains addressable.
    /// </summary>
    /// <returns>
    /// The parsed values keyed by option name, the positional tokens, and a usage error message
    /// (null when parsing succeeded). A repeated option is an error rather than a silent
    /// last-one-wins, so a mistyped invocation cannot quietly change the requested page.
    /// </returns>
    public static (IReadOnlyDictionary<string, string> Options, IReadOnlyList<string> Positional, string? Error)
        Parse(IReadOnlyList<string> args, IReadOnlySet<string> knownOptions)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(knownOptions);

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (token == "--")
            {
                for (i++; i < args.Count; i++)
                    positional.Add(args[i]);
                break;
            }

            if (token.Length == 0 || token[0] != '-')
            {
                positional.Add(token);
                continue;
            }

            var separator = token.IndexOf('=');
            var name = separator >= 0 ? token[..separator] : token;
            if (!knownOptions.Contains(name))
                return (options, positional, $"unknown option '{name}'.");

            string value;
            if (separator >= 0)
            {
                value = token[(separator + 1)..];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            else
            {
                return (options, positional, $"option {name} requires a value.");
            }

            if (value.Length == 0)
                return (options, positional, $"option {name} requires a value.");

            if (!options.TryAdd(name, value))
                return (options, positional, $"option {name} was given more than once.");

            continue;
        }

        return (options, positional, null);
    }
}