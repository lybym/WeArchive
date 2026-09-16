using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Services;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Contract tests for the shipped CLI artifact shape (Issue #9 / M0.5).
/// <para>
/// The product surface is a single self-contained <c>win-x64</c> CLI whose assembly is
/// named <c>WeArchive</c>, so the published file is <c>WeArchive.exe</c> — the name
/// <c>docs/CLI.md</c>, <c>README.md</c>, <c>docs/ARCHITECTURE.md</c> and the packaging
/// scripts all document. These tests fail loudly if the assembly is renamed again, if the
/// version contract breaks for a release-stamped or prerelease build, if the documented
/// command family disappears from help, or if the retired WPF/Velopack distribution surface
/// is reintroduced.
/// </para>
/// </summary>
public sealed class CliPackagingTests
{
    /// <summary>
    /// A released version: three numeric parts plus an optional SemVer prerelease suffix, with
    /// no build metadata. `docs/CLI.md` documents `--version` as the product version, and
    /// `release.yml` accepts a suffix to publish a prerelease.
    /// </summary>
    private const string ReleasedVersionPattern = @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$";

    /// <summary>
    /// The published executable is <c>WeArchive.exe</c>; packaging and PATH instructions
    /// depend on this exact assembly name (docs/adr/0007-cli-self-contained-distribution.md).
    /// </summary>
    [Fact]
    public void CliAssemblyIsNamedWeArchive()
    {
        Assert.Equal("WeArchive", typeof(CliHost).Assembly.GetName().Name);
    }

    /// <summary>
    /// The version document keeps the documented `version`/`framework`/`platform` shape and a
    /// released version, because release automation and the artifact smoke test read it.
    /// </summary>
    [Fact]
    public async Task VersionDocumentKeepsTheDocumentedShape()
    {
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await new VersionCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        var version = doc.RootElement.GetProperty("version").GetString();
        Assert.NotNull(version);
        Assert.Matches(ReleasedVersionPattern, version);
        Assert.Equal("net10.0-windows", doc.RootElement.GetProperty("framework").GetString());
        Assert.Equal("win-x64", doc.RootElement.GetProperty("platform").GetString());
    }

    /// <summary>
    /// The running assembly resolves to the same value the version command reports, and that
    /// value is a release version — not the SDK's <c>+&lt;commit&gt;</c>-suffixed informational
    /// string and not the numeric-only assembly version.
    /// </summary>
    [Fact]
    public void RunningAssemblyResolvesToAReleaseVersion()
    {
        var version = ProductVersion.Current;

        Assert.Matches(ReleasedVersionPattern, version);
        Assert.DoesNotContain('+', version);
    }

    /// <summary>
    /// Build metadata is stripped, so a version identifies the release and not the commit it
    /// happened to be built from. The SDK appends <c>+&lt;source revision id&gt;</c> for any git
    /// checkout, which every CI build is.
    /// </summary>
    [Theory]
    [InlineData("0.1.0+be7071540fb4cdd1308fd73e2da6e1e7edc171b8", "0.1.0")]
    [InlineData("0.2.0-rc.1+be7071540fb4cdd1308fd73e2da6e1e7edc171b8", "0.2.0-rc.1")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.2.0-rc.1", "0.2.0-rc.1")]
    [InlineData("  0.1.0  ", "0.1.0")]
    public void BuildMetadataIsStrippedFromInformationalVersions(string raw, string expected)
    {
        Assert.Equal(expected, ProductVersion.Normalize(raw));
    }

    /// <summary>
    /// A build that stamped no usable informational version falls back to the numeric assembly
    /// version rather than reporting an empty or malformed version.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+be7071540fb4cdd1308fd73e2da6e1e7edc171b8")]
    public void UnusableInformationalVersionsNormalizeToNull(string raw)
    {
        Assert.Null(ProductVersion.Normalize(raw));
    }

    [Fact]
    public void NullInformationalVersionNormalizesToNull()
    {
        Assert.Null(ProductVersion.Normalize(null));
    }

    /// <summary>
    /// A prerelease stays identifiable: reporting the numeric assembly version would make
    /// `0.2.0-rc.1` indistinguishable from the final `0.2.0`, and `release.yml` deliberately
    /// publishes prereleases (`--prerelease`).
    /// </summary>
    [Fact]
    public void PrereleaseVersionsRemainIdentifiable()
    {
        Assert.Equal("0.2.0-rc.1", ProductVersion.Normalize("0.2.0-rc.1+abc1234"));
        Assert.NotEqual("0.2.0", ProductVersion.Normalize("0.2.0-rc.1+abc1234"));
    }

    /// <summary>
    /// An assembly that stamped an informational version reports it (minus build metadata); an
    /// assembly that stamped none falls back to its numeric version. Both branches are pinned
    /// against synthetic assemblies so the rule is verified independently of this build's own
    /// version stamp.
    /// </summary>
    [Fact]
    public void InformationalVersionWinsAndNumericVersionIsTheFallback()
    {
        var withInformational = SyntheticAssembly.Create("1.2.3.0", "1.2.3-rc.4+buildmeta");
        Assert.Equal("1.2.3-rc.4", ProductVersion.Of(withInformational));

        var withoutInformational = SyntheticAssembly.Create("1.2.3.0", informationalVersion: null);
        Assert.Equal("1.2.3", ProductVersion.Of(withoutInformational));
    }

    /// <summary>
    /// Every FR-22 command family must be reachable from the shipped artifact. This is the
    /// assembly-level counterpart of the released-artifact smoke test in
    /// <c>scripts/smoke-test-cli.ps1</c>.
    /// </summary>
    [Fact]
    public void RouterExposesTheRequiredCommandFamily()
    {
        var router = new CommandRouter(new ServiceCollection().BuildServiceProvider());

        foreach (var required in new[] { "version", "doctor", "account", "conversation", "sync", "export" })
        {
            Assert.Contains(required, router.CommandNames);
        }
    }

    /// <summary>No project may depend on the retired Velopack distribution stack.</summary>
    [Fact]
    public void NoProjectReferencesVelopack()
    {
        var root = TestRepository.FindRoot();

        var projectFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "Directory.Packages.props"));

        foreach (var file in projectFiles)
        {
            Assert.DoesNotContain(
                "Velopack",
                File.ReadAllText(file),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The WPF product surface must not exist anywhere in the target solution.</summary>
    [Fact]
    public void TheRetiredWpfProductSurfaceIsGone()
    {
        var root = TestRepository.FindRoot();

        Assert.False(Directory.Exists(Path.Combine(root, "src", "WeArchive.App")));
        Assert.False(File.Exists(Path.Combine(root, "scripts", "pack-velopack.ps1")));
        Assert.DoesNotContain(
            "WeArchive.App",
            File.ReadAllText(Path.Combine(root, "WeArchive.sln")),
            StringComparison.Ordinal);
    }
}
