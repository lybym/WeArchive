using WeArchive.Core.Abstractions;

namespace WeArchive.Infrastructure;

/// <summary>System clock. The only place wall-clock time enters the application.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
}
