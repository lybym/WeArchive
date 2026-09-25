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
    public void CurrentManifestVersionIsTwo()
    {
        var manifest = BuildManifest();
        Assert.Equal(2, manifest.ManifestVersion);
        Assert.Equal(2, RawManifest.CurrentManifestVersion);
    }

    [Fact]
    public void VersionOneManifestRemainsReadableWithoutCaptureCheckpoint()
    {
        var old = BuildManifest() with { ManifestVersion = 1 };
        var restored = RawManifestSerializer.TryDeserialize(RawManifestSerializer.Serialize(old));
        Assert.NotNull(restored);
        Assert.Null(restored.CaptureCheckpoint);
    }

    [Fact]
    public void VersionTwoCheckpointRoundTripsAndRejectsWrongGeneration()
    {
        var manifest = BuildManifest() with
        {
            Coverage = [new RawPartitionCoverage
            {
                PartitionId = "partition-1", Status = RawPartitionStatus.Captured,
                SourceFingerprint = "source-hash", ArtifactSha256 = "abc123",
            }],
            CaptureCheckpoint = new RawCaptureCheckpoint
            {
                GenerationId = "gen_abcdef0123456789", CaptureAdapterFamily = "fixture",
                CaptureAdapterVersion = "1.0.0",
                PartitionFingerprints = new Dictionary<string, string> { ["partition-1"] = "source-hash" },
            },
        };
        var json = RawManifestSerializer.Serialize(manifest);
        Assert.NotNull(RawManifestSerializer.TryDeserialize(json)?.CaptureCheckpoint);
        Assert.Null(RawManifestSerializer.TryDeserialize(RawManifestSerializer.Serialize(manifest with
        {
            CaptureCheckpoint = manifest.CaptureCheckpoint! with { GenerationId = "wrong" },
        })));
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

    [Fact]
    public void CoverageAndCheckpointMustAgreeOrTheManifestIsRejected()
    {
        var manifest = BuildManifest() with
        {
            Coverage = [Partition("p1", "hash-1")],
            CaptureCheckpoint = new RawCaptureCheckpoint
            {
                GenerationId = "gen_abcdef0123456789",
                CaptureAdapterFamily = "fixture",
                CaptureAdapterVersion = "1.0.0",
                PartitionFingerprints = new Dictionary<string, string> { ["p1"] = "hash-1" },
            },
        };
        Assert.NotNull(Deserialize(manifest));

        // A checkpoint fingerprint that disagrees with the recorded coverage proves nothing.
        Assert.Null(Deserialize(manifest with
        {
            CaptureCheckpoint = manifest.CaptureCheckpoint! with
            {
                PartitionFingerprints = new Dictionary<string, string> { ["p1"] = "tampered" },
            },
        }));

        // A checkpoint whose partition set or adapter identity differs from the generation.
        Assert.Null(Deserialize(manifest with
        {
            CaptureCheckpoint = manifest.CaptureCheckpoint! with
            {
                PartitionFingerprints = new Dictionary<string, string>
                {
                    ["p1"] = "hash-1",
                    ["p2"] = "hash-2",
                },
            },
        }));
        Assert.Null(Deserialize(manifest with
        {
            CaptureCheckpoint = manifest.CaptureCheckpoint! with { CaptureAdapterVersion = "9.9.9" },
        }));

        // Coverage that references no artifact of the generation cannot be verified.
        Assert.Null(Deserialize(manifest with { Coverage = [Partition("p1", "hash-1", "no-such-artifact")] }));

        // Duplicate partition ids make coverage ambiguous.
        Assert.Null(Deserialize(manifest with
        {
            Coverage = [Partition("p1", "hash-1"), Partition("p1", "hash-1")],
        }));
    }

    private static RawPartitionCoverage Partition(
        string partitionId,
        string fingerprint,
        string artifactSha256 = "abc123") => new()
    {
        PartitionId = partitionId,
        Status = RawPartitionStatus.Captured,
        SourceFingerprint = fingerprint,
        ArtifactSha256 = artifactSha256,
    };

    private static RawManifest? Deserialize(RawManifest manifest) =>
        RawManifestSerializer.TryDeserialize(RawManifestSerializer.Serialize(manifest));
}
