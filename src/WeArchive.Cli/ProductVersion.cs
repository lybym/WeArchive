using System.Reflection;

namespace WeArchive.Cli;

/// <summary>
/// The single source of truth for the product version reported by the CLI.
/// <para>
/// Both surfaces that can print a version — the <c>wearchive version</c> command and the
/// top-level <c>--version</c> flag handled by <see cref="CommandLine.CliHost"/> — resolve it
/// here, so the two can never drift apart.
/// </para>
/// <para>
/// The value is the assembly's informational version, because that is the only one the SDK
/// stamps with a release's full version: <c>-p:Version=0.2.0-rc.1</c> produces
/// <c>AssemblyVersion=0.2.0.0</c> but <c>AssemblyInformationalVersion=0.2.0-rc.1</c>. Reporting
/// the numeric assembly version instead would make a prerelease indistinguishable from its
/// final release — and <c>release.yml</c> deliberately publishes prereleases.
/// </para>
/// </summary>
internal static class ProductVersion
{
    /// <summary>The version of the running CLI assembly.</summary>
    public static string Current => Of(typeof(ProductVersion).Assembly);

    /// <summary>
    /// Resolves <paramref name="assembly"/>'s product version: its informational version with
    /// build metadata removed, falling back to the numeric assembly version when the build
    /// stamped no usable informational version.
    /// </summary>
    public static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = Normalize(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        if (informational is not null)
        {
            return informational;
        }

        var numeric = assembly.GetName().Version;
        return numeric is null ? "0.0.0" : numeric.ToString(3);
    }

    /// <summary>
    /// Normalizes a raw informational version for display, or returns <see langword="null"/>
    /// when it carries nothing usable. The SDK appends <c>+&lt;source revision id&gt;</c> for a
    /// git checkout, which identifies the commit rather than the release, so it is dropped and
    /// the remaining value is the release version a caller can compare against a tag.
    /// </summary>
    public static string? Normalize(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
        return version.Length == 0 ? null : version;
    }
}
