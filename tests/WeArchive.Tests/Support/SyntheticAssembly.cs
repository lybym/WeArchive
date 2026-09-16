using System.Reflection;
using System.Reflection.Emit;

namespace WeArchive.Tests.Support;

/// <summary>
/// Builds throwaway in-memory assemblies carrying chosen version attributes, so version
/// resolution can be verified for values a build cannot be given at test time. Used by
/// <c>CliPackagingTests</c> to pin <c>ProductVersion.Of</c> for both the informational-version
/// and numeric-fallback branches.
/// </summary>
internal static class SyntheticAssembly
{
    public static Assembly Create(string assemblyVersion, string? informationalVersion)
    {
        var name = new AssemblyName($"WeArchive.Synthetic.{Guid.NewGuid():n}")
        {
            Version = Version.Parse(assemblyVersion),
        };

        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        if (informationalVersion is not null)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
                [informationalVersion]));
        }

        // Force the assembly to materialize so GetCustomAttribute can read it.
        builder.DefineDynamicModule("main");
        return builder;
    }
}
