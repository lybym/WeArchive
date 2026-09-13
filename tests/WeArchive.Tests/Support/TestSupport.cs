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
