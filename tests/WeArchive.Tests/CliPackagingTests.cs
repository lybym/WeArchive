using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
/// documented command family disappears from help, or if the retired WPF/Velopack
/// distribution surface is reintroduced.
/// </para>
/// </summary>
public sealed class CliPackagingTests
{
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
    /// The version document keeps the documented `version`/`framework`/`platform` shape and
    /// a three-part version, because release automation and the artifact smoke test read it.
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
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal("net10.0-windows", doc.RootElement.GetProperty("framework").GetString());
        Assert.Equal("win-x64", doc.RootElement.GetProperty("platform").GetString());
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
        var root = TestRepository.TryFindRoot();
        if (root is null)
        {
            return; // Not running from a checkout; CI's direct build/test step covers this.
        }

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
        var root = TestRepository.TryFindRoot();
        if (root is null)
        {
            return; // Not running from a checkout; CI's direct build/test step covers this.
        }

        Assert.False(Directory.Exists(Path.Combine(root, "src", "WeArchive.App")));
        Assert.False(File.Exists(Path.Combine(root, "scripts", "pack-velopack.ps1")));
        Assert.DoesNotContain(
            "WeArchive.App",
            File.ReadAllText(Path.Combine(root, "WeArchive.sln")),
            StringComparison.Ordinal);
    }
}
