namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Parses global options from <c>argv</c> and leaves the remaining positional/unknown
/// arguments for the command. Global options may appear before or after the command name,
/// matching the documented <c>wearchive &lt;command&gt; ... --json --no-input</c> usage.
/// </summary>
public static class CommandLineParser
{
    /// <summary>
    /// Splits <paramref name="args"/> into parsed global options and the remaining
    /// arguments (positional command name + command-specific options/values).
    /// </summary>
    public static (GlobalOptions Options, IReadOnlyList<string> Remaining) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var json = false;
        var quiet = false;
        var noInput = false;
        var showVersion = false;
        var showHelp = false;

        var remaining = new List<string>(args.Count);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--quiet":
                case "-q":
                    quiet = true;
                    break;
                case "--no-input":
                    noInput = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    remaining.Add(arg);
                    break;
            }
        }

        // --version and --help are mutually exclusive top-level intents; --version wins
        // because a caller asking for version is doing a cheap probe and does not want
        // help text.
        if (showVersion)
            showHelp = false;

        var options = new GlobalOptions
        {
            Json = json,
            Quiet = quiet,
            NoInput = noInput,
            ShowVersion = showVersion,
            ShowHelp = showHelp,
        };

        return (options, remaining);
    }
}
