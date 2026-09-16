using WeArchive.Core.Abstractions;

namespace WeArchive.Tests.Support;

/// <summary>Fixed clock so exports and import runs are reproducible in tests.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public static readonly DateTimeOffset Default = new(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8));

    public FixedClock()
        : this(Default)
    {
    }

    public DateTimeOffset UtcNow { get; set; } = now;

    public TimeSpan LocalOffset => TimeSpan.FromHours(8);
}

/// <summary>Locates the repository root from the test assembly location.</summary>
internal static class TestRepository
{
    /// <summary>
    /// Walks up from the test assembly directory (…/tests/WeArchive.Tests/bin/&lt;config&gt;/&lt;tfm&gt;)
    /// until the solution file is found, so packaging/architecture tests can assert on the
    /// repository layout without an absolute machine-specific path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The test assembly is not running from inside a checkout. This throws instead of
    /// returning null so a repository-layout guarantee can never become a vacuous assertion —
    /// silently skipping is exactly how a reintroduced WPF/Velopack surface would slip through.
    /// </exception>
    public static string FindRoot()
    {
        var start = Path.GetDirectoryName(typeof(TestRepository).Assembly.Location);
        var directory = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WeArchive.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate WeArchive.sln above '{start}'. Repository-layout tests must run " +
            "from the test project's output directory inside the checkout.");
    }
}

/// <summary>Creates and disposes an isolated scratch directory for one test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "wearchive-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
