using System.Text.Json;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure.RawVault;

namespace WeArchive.Tests;

/// <summary>
/// Manifest serialization/version validation and invalid-manifest rejection tests.
/// Issue #22 acceptance criteria: "A successful capture produces a complete, parseable,
/// versioned manifest" and "Fatal source/WAL consistency or required-artifact coverage
/// failure is never reported/published as a complete generation."
/// </summary>
public sealed class RawVaultManifestTests
{
    private static RawManifest BuildManifest() => new()
    {
        GenerationId = "gen_abcdef0123456789",
        AccountId = "a_0123456789abcdef",
        SourceProfileId = "wxid_test",
        Source = new RawManifestSource
        {
            AdapterName = "fixture",
            AdapterVersion = "1.0.0",
            SourceProductName = "Fixture source",
            SourceVersion = "fixture-1",
        },
        Capture = new RawManifestCapture
        {
            CaptureTime = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8)),
            CaptureAdapterFamily = "fixture",
            CaptureAdapterVersion = "1.0.0",
            Mode = RawCaptureMode.Baseline,
            Completeness = RawGenerationCompleteness.Complete,
            ArtifactCount = 1,
        },
        Artifacts =
        [
            new RawArtifactDescriptor
            {
                Role = "source-database",
                Name = "message_0.db",
                ContentRef = "artifacts/abc123.db",
                Sha256 = "abc123",
                Size = 4096,
                SourceFormat = "sqlite",
                IsDecrypted = true,
            },
        ],
        PreviousGenerationId = null,
    };

    [Fact]
    public void ManifestVersionIsOne()
    {
        var manifest = BuildManifest();
        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal(1, RawManifest.CurrentManifestVersion);
    }

    [Fact]
    public void RoundTripPreservesAllFields()
    {
        var manifest = BuildManifest();
        var json = RawManifestSerializer.Serialize(manifest);
        var restored = RawManifestSerializer.TryDeserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(manifest.GenerationId, restored!.GenerationId);
        Assert.Equal(manifest.AccountId, restored.AccountId);
        Assert.Equal(manifest.SourceProfileId, restored.SourceProfileId);
        Assert.Equal(manifest.Source.AdapterName, restored.Source.AdapterName);
        Assert.Equal(manifest.Capture.CaptureTime, restored.Capture.CaptureTime);
        Assert.Equal(manifest.Capture.Completeness, restored.Capture.Completeness);
        Assert.Equal(manifest.Artifacts.Count, restored.Artifacts.Count);
        Assert.Equal(manifest.Artifacts[0].Sha256, restored.Artifacts[0].Sha256);
        Assert.Equal(manifest.PreviousGenerationId, restored.PreviousGenerationId);
    }

    [Fact]
    public void SerializedManifestUsesSnakeCase()
    {
        var manifest = BuildManifest();
        var json = RawManifestSerializer.Serialize(manifest);

        Assert.Contains("\"manifest_version\"", json);
        Assert.Contains("\"vault_format_version\"", json);
        Assert.Contains("\"generation_id\"", json);
        Assert.Contains("\"source_profile_id\"", json);
        Assert.Contains("\"capture_time\"", json);
        Assert.Contains("\"previous_generation_id\"", json);
    }

    [Fact]
    public void TryDeserializeRejectsUnknownVersion()
    {
        var json = """
        {
            "manifest_version": 99,
            "vault_format_version": 1,
            "generation_id": "gen_test",
            "account_id": "a_test",
            "source_profile_id": "wxid",
            "source": { "adapter_name": "x", "adapter_version": "1" },
            "capture": {
                "capture_time": "2026-03-01T12:00:00+08:00",
                "capture_adapter_family": "x",
                "capture_adapter_version": "1",
                "mode": 0,
                "completeness": 0,
                "artifact_count": 0
            },
            "artifacts": []
        }
        """;
        Assert.Null(RawManifestSerializer.TryDeserialize(json));
    }

    [Fact]
    public void TryDeserializeRejectsMalformedJson()
    {
        Assert.Null(RawManifestSerializer.TryDeserialize("not json"));
        Assert.Null(RawManifestSerializer.TryDeserialize("{ broken"));
    }

    [Fact]
    public void TryDeserializeRejectsMissingGenerationId()
    {
        var json = """
        {
            "manifest_version": 1,
            "vault_format_version": 1,
            "generation_id": "",
            "account_id": "a_test",
            "source_profile_id": "wxid",
            "source": { "adapter_name": "x", "adapter_version": "1" },
            "capture": {
                "capture_time": "2026-03-01T12:00:00+08:00",
                "capture_adapter_family": "x",
                "capture_adapter_version": "1",
                "mode": 0,
                "completeness": 0,
                "artifact_count": 0
            },
            "artifacts": []
        }
        """;
        Assert.Null(RawManifestSerializer.TryDeserialize(json));
    }

    [Fact]
    public void DiagnosticsRoundTripWithSeverity()
    {
        var manifest = BuildManifest() with
        {
            Diagnostics =
            [
                RawManifestDiagnostic.Fatal("partition_unreadable", "msg"),
                RawManifestDiagnostic.Partial("wal_frames_rejected", "msg2"),
                RawManifestDiagnostic.Info("source_discovered", "msg3"),
            ],
        };

        var json = RawManifestSerializer.Serialize(manifest);
        var restored = RawManifestSerializer.TryDeserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Diagnostics.Count);
        Assert.Equal("fatal", restored.Diagnostics[0].Severity);
        Assert.Equal("partition_unreadable", restored.Diagnostics[0].Code);
        Assert.Equal("partial", restored.Diagnostics[1].Severity);
        Assert.Equal("info", restored.Diagnostics[2].Severity);
    }
}
