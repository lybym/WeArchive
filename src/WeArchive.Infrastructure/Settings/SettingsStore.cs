using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeArchive.Infrastructure.Settings;

/// <summary>User settings. Never contains chat content or archive keys.</summary>
public sealed record AppSettings
{
    /// <summary>Directory the export package is written to.</summary>
    [JsonPropertyName("export_directory")]
    public string? ExportDirectory { get; init; }

    /// <summary>Last used source profile, so the account selection is remembered.</summary>
    [JsonPropertyName("source_profile_id")]
    public string? SourceProfileId { get; init; }
}

/// <summary>
/// Persists <see cref="AppSettings"/> next to the archive. A corrupt or unreadable file
/// falls back to defaults rather than failing application start-up.
/// </summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (IOException)
        {
            // Settings are a convenience; failing to persist them must not break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
