using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.RawVault;

namespace WeArchive.Tests;

public sealed class VaultInspectionTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task LegacyReferencesAndPhysicalFilesRemainDistinct(int version)
    {
        using var fixture = new VaultFixture();
        fixture.AddV1("g1", version: version);
        fixture.AddV1("g2", "g1", version);
        var result = await fixture.Inspect();
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Metrics!["logical_artifact_references"].Value);
        Assert.Equal(fixture.Bytes.Length * 2, result.Metrics["logical_generation_bytes"].Value);
        Assert.Equal(fixture.Bytes.Length * 2, result.Metrics["v1_whole_artifact_bytes"].Value);
        Assert.Equal(0, result.Metrics["v2_pack_logical_bytes"].Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SharedMixedAndOrphanObjectsHaveObservedNonOverlappingAccounting(bool mixed)
    {
        using var fixture = new VaultFixture();
        if (mixed) fixture.AddV1("g0");
        var artifact = fixture.AddV2("g1", mixed ? "g0" : null);
        fixture.AddV2("g2", "g1");
        var orphan = new RawVaultV2Format.StoredObject(1, RawVaultV2Format.DataDigest([7, 8, 9]), [7, 8, 9]);
        var packDirectory = Path.Combine(fixture.Account, "objects", "packs");
        File.WriteAllBytes(Path.Combine(packDirectory, "orphan.rvpk"), RawVaultV2Format.BuildPack([orphan]));
        var initial = Directory.GetFiles(packDirectory).First();
        File.Copy(initial, Path.Combine(packDirectory, "duplicate.rvpk"));
        var before = fixture.Snapshot();
        var result = await fixture.Inspect();
        Assert.True(result.Succeeded);
        var metrics = result.Metrics!;
        Assert.Equal(fixture.Bytes.Length * (mixed ? 3 : 2), metrics["logical_generation_bytes"].Value);
        Assert.Equal(4096 + 3, metrics["v2_unique_payload_uncompressed_bytes"].Value);
        Assert.Equal(4096, metrics["v2_reachable_payload_uncompressed_bytes"].Value);
        Assert.Equal(3, metrics["v2_orphan_payload_uncompressed_bytes"].Value);
        Assert.Equal(artifact.Objects.Single(o => o.Kind == 2).Bytes.Length, metrics["v2_map_metadata_uncompressed_bytes"].Value);
        Assert.True(metrics["v2_unique_payload_stored_bytes"].Value < metrics["v2_unique_payload_uncompressed_bytes"].Value);
        Assert.Equal(artifact.Objects.Count, metrics["duplicate_physical_records"].Value);
        Assert.Equal(Directory.GetFiles(packDirectory).Sum(p => new FileInfo(p).Length), metrics["v2_pack_logical_bytes"].Value);
        Assert.All(metrics.Values, m => Assert.NotEqual("estimated", m.Basis));
        Assert.Null(metrics["source_changed_page_bytes"].Value);
        Assert.Equal("not_applicable", metrics["storage_write_amplification"].Basis);
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(metrics["v2_pack_allocated_bytes"].Value);
            Assert.True(metrics["v2_pack_allocated_bytes"].Value > metrics["v2_pack_logical_bytes"].Value);
        }
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("missing")] [InlineData("corrupt")] [InlineData("inconsistent")]
    [InlineData("wal")]
    public async Task DerivedIndexIsNeverAuthorityOrModified(string state)
    {
        using var fixture = new VaultFixture();
        fixture.AddV2("g1");
        var index = Path.Combine(fixture.Account, "objects", "lookup.sqlite");
        if (state == "wal") File.WriteAllBytes(index + "-wal", [1, 2, 3]);
        else if (state == "missing") File.Delete(index);
        else if (state == "corrupt") File.WriteAllBytes(index, [1, 2, 3]);
        else
        {
            using var connection = new SqliteConnection($"Data Source={index};Pooling=False");
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE objects SET record_offset=17"; command.ExecuteNonQuery();
        }
        var before = fixture.Snapshot();
        var result = await fixture.Inspect();
        Assert.True(result.Succeeded);
        Assert.Equal((state == "wal" ? "inconsistent" : state) + "_rebuildable", Assert.Single(result.Accounts).DerivedIndexStatus);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("stats", false)] [InlineData("verify", false)]
    [InlineData("stats", true)] [InlineData("verify", true)]
    public async Task RealWalIndexWithoutSidecarsCannotCreateOrChangeVaultFiles(string operation, bool v2)
    {
        using var fixture = new VaultFixture();
        if (v2) fixture.AddV2("g1"); else fixture.AddV1("g1");
        var index = Path.Combine(fixture.Account, "objects", "lookup.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        using (var connection = new SqliteConnection($"Data Source={index};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            Assert.Equal("wal", command.ExecuteScalar());
            command.CommandText = "CREATE TABLE IF NOT EXISTS objects(kind INTEGER,digest BLOB,pack_file TEXT,record_offset INTEGER,record_length INTEGER)";
            command.ExecuteNonQuery();
        }
        Assert.Equal(2, File.ReadAllBytes(index)[18]);
        Assert.False(File.Exists(index + "-wal")); Assert.False(File.Exists(index + "-shm"));
        var before = fixture.Snapshot();
        var result = await RunCli(fixture, ["vault", operation, "--json", "--no-input"]);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal("inconsistent_rebuildable", json.RootElement.GetProperty("accounts")[0].GetProperty("derived_index_status").GetString());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("NULL,zeroblob(32),'pack.rvpk',16,48")]
    [InlineData("1,NULL,'pack.rvpk',16,48")]
    [InlineData("1,zeroblob(32),NULL,16,48")]
    [InlineData("1,zeroblob(32),'pack.rvpk',NULL,48")]
    [InlineData("1,zeroblob(32),'pack.rvpk',16,NULL")]
    [InlineData("257,zeroblob(32),'pack.rvpk',16,48")]
    [InlineData("-1,zeroblob(32),'pack.rvpk',16,48")]
    [InlineData("1,zeroblob(31),'pack.rvpk',16,48")]
    [InlineData("1,'not-a-blob','pack.rvpk',16,48")]
    [InlineData("1,zeroblob(32),17,16,48")]
    [InlineData("1,zeroblob(32),'../pack.rvpk',16,48")]
    [InlineData("1,zeroblob(32),'pack.rvpk',-1,48")]
    [InlineData("1,zeroblob(32),'pack.rvpk','bad-offset',48")]
    [InlineData("1,zeroblob(32),'pack.rvpk',16,2147483648")]
    [InlineData("1,zeroblob(32),'pack.rvpk',16,47")]
    [InlineData("1,zeroblob(32),'pack.rvpk',16,48.5")]
    [InlineData("'bad-kind',zeroblob(32),'pack.rvpk',16,48")]
    [InlineData("missing_schema")]
    public async Task MalformedDerivedRowsAndSchemasCannotFailValidAuthority(string values)
    {
        using var fixture = new VaultFixture(); fixture.AddV2("g1");
        var index = Path.Combine(fixture.Account, "objects", "lookup.sqlite");
        using (var connection = new SqliteConnection($"Data Source={index};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE objects; CREATE TABLE objects(kind,digest,pack_file,record_offset,record_length)";
            command.ExecuteNonQuery();
            command.CommandText = values == "missing_schema" ? "DROP TABLE objects; CREATE TABLE objects(unrelated)" : $"INSERT INTO objects VALUES ({values})";
            command.ExecuteNonQuery();
        }
        var before = fixture.Snapshot();
        foreach (var operation in new[] { "stats", "verify" })
        {
            var result = await RunCli(fixture, ["vault", operation, "--json", "--no-input"]);
            Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
            using var json = JsonDocument.Parse(result.Output);
            Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal("corrupt_rebuildable", json.RootElement.GetProperty("accounts")[0].GetProperty("derived_index_status").GetString());
            Assert.Equal(before, fixture.Snapshot());
        }
    }

    [Theory]
    [InlineData("missing_pack")] [InlineData("corrupt_pack")] [InlineData("missing_map")]
    [InlineData("missing_data")] [InlineData("bad_sha")] [InlineData("manifest_version")]
    [InlineData("vault_version")] [InlineData("object_version")] [InlineData("bad_map")]
    [InlineData("missing_manifest")] [InlineData("corrupt_manifest")] [InlineData("missing_predecessor")]
    [InlineData("lineage_cycle")]
    [InlineData("noncanonical_map")]
    public async Task AuthoritativeCorruptionFailsExplicitlyWithoutVerifiedTotals(string damage)
    {
        using var fixture = new VaultFixture();
        var artifact = fixture.AddV2("g1");
        var manifest = fixture.ReadManifest("g1");
        var pack = Directory.GetFiles(Path.Combine(fixture.Account, "objects", "packs")).Single();
        if (damage == "missing_pack") File.Delete(pack);
        else if (damage == "corrupt_pack") File.WriteAllBytes(pack, [0]);
        else if (damage == "missing_map" || damage == "missing_data")
            File.WriteAllBytes(pack, RawVaultV2Format.BuildPack(artifact.Objects.Where(o => o.Kind != (damage == "missing_map" ? 2 : 1)).ToArray()));
        else if (damage == "object_version")
        {
            var bytes = File.ReadAllBytes(pack); bytes[20] = 99;
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - 56)).CopyTo(bytes, bytes.Length - 32);
            File.WriteAllBytes(pack, bytes);
        }
        else if (damage == "noncanonical_map")
        {
            var extraRoot = RawVaultV2Format.EncodeInternal(1, [new(artifact.Root, artifact.BlockCount, (ulong)artifact.Size)]);
            var map = new RawVaultV2Format.StoredObject(2, RawVaultV2Format.MapDigest(extraRoot), extraRoot);
            File.WriteAllBytes(pack, RawVaultV2Format.BuildPack(artifact.Objects.Append(map).ToArray()));
            manifest = manifest with { Artifacts = [manifest.Artifacts[0] with { Storage = manifest.Artifacts[0].Storage! with { Root = Convert.ToHexString(map.Digest).ToLowerInvariant() } }] };
        }
        else if (damage == "bad_map")
        {
            var invalid = RawVaultV2Format.EncodeLeaf([new(artifact.Objects.First(o => o.Kind == 1).Digest, 1)]);
            var map = new RawVaultV2Format.StoredObject(2, RawVaultV2Format.MapDigest(invalid), invalid);
            File.WriteAllBytes(pack, RawVaultV2Format.BuildPack(artifact.Objects.Where(o => o.Kind == 1).Append(map).ToArray()));
            manifest = manifest with { Artifacts = [manifest.Artifacts[0] with { Storage = manifest.Artifacts[0].Storage! with { Root = Convert.ToHexString(map.Digest).ToLowerInvariant() } }] };
        }
        else if (damage == "bad_sha") manifest = manifest with { Artifacts = [manifest.Artifacts[0] with { Sha256 = new string('0', 64) }] };
        else if (damage == "manifest_version") manifest = manifest with { ManifestVersion = 99 };
        else if (damage == "vault_version") manifest = manifest with { VaultFormatVersion = 99 };
        else if (damage == "missing_predecessor") manifest = manifest with { PreviousGenerationId = "missing" };
        else if (damage == "lineage_cycle") manifest = manifest with { PreviousGenerationId = "g1" };
        fixture.WriteManifest("g1", manifest);
        if (damage == "missing_manifest") File.Delete(fixture.ManifestPath("g1"));
        if (damage == "corrupt_manifest") File.WriteAllText(fixture.ManifestPath("g1"), "{");
        var before = fixture.Snapshot();
        var result = await fixture.Inspect();
        Assert.False(result.Succeeded); Assert.Null(result.Metrics);
        Assert.Equal("account", Assert.Single(result.Failures).AccountId);
        Assert.NotEmpty(result.Failures[0].Message);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("sha")] [InlineData("size")] [InlineData("missing")]
    public async Task LegacyValidationStillFailsClosed(string damage)
    {
        using var fixture = new VaultFixture(); fixture.AddV1("g1");
        var path = Path.Combine(fixture.Account, "generations", "g1", "data");
        if (damage == "sha") File.WriteAllBytes(path, new byte[fixture.Bytes.Length]);
        if (damage == "size") File.WriteAllBytes(path, [1]);
        if (damage == "missing") File.Delete(path);
        var result = await fixture.Inspect();
        Assert.False(result.Succeeded); Assert.Null(result.Metrics);
        Assert.Equal("payload", Assert.Single(result.Failures).Artifact);
    }

    [Fact]
    public async Task CorruptOrphanPackCannotBeHiddenByVerifiedGenerations()
    {
        using var fixture = new VaultFixture(); fixture.AddV2("g1");
        File.WriteAllBytes(Path.Combine(fixture.Account, "objects", "packs", "orphan.rvpk"), [1, 2, 3]);
        var result = await fixture.Inspect();
        Assert.False(result.Succeeded); Assert.Null(result.Metrics);
        Assert.Contains("orphan.rvpk", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public async Task IdenticalObjectsInDifferentAccountsAreNotDeduplicated()
    {
        using var fixture = new VaultFixture(); fixture.AddV2("g1");
        var second = Path.Combine(fixture.Root, "accounts", "second");
        var packs = Path.Combine(second, "objects", "packs"); Directory.CreateDirectory(packs);
        foreach (var pack in Directory.GetFiles(Path.Combine(fixture.Account, "objects", "packs")))
            File.Copy(pack, Path.Combine(packs, Path.GetFileName(pack)));
        var generation = Path.Combine(second, "generations", "g1"); Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, "manifest.json"), RawManifestSerializer.Serialize(fixture.ReadManifest("g1") with { AccountId = "second" }));
        var result = await fixture.Inspect();
        Assert.True(result.Succeeded);
        Assert.Equal(8192, result.Metrics!["v2_unique_payload_uncompressed_bytes"].Value);
        Assert.Equal(2, result.Metrics["account_count"].Value);
    }

    [Fact]
    public async Task EmptyVaultAndStagingBytesAreExplicitAndNoTransientCacheIsCountedAsEvidence()
    {
        using var fixture = new VaultFixture(); Directory.CreateDirectory(fixture.Root);
        var empty = await fixture.Inspect();
        Assert.True(empty.Succeeded); Assert.Equal(0, empty.Metrics!["logical_generation_bytes"].Value);
        var staging = Path.Combine(fixture.Account, "generations", "unfinished.staging"); Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "scratch"), [1, 2, 3]);
        var staged = await fixture.Inspect();
        Assert.True(staged.Succeeded); Assert.Equal(3, staged.Metrics!["vault_staging_bytes"].Value);
        Assert.Equal(0, staged.Metrics["generation_count"].Value);
    }

    [Fact]
    public async Task CliJsonSuccessFailureCancellationAndUsageContracts()
    {
        using var fixture = new VaultFixture(); fixture.AddV2("g1");
        foreach (var operation in new[] { "stats", "verify" })
        {
            var (exit, output, error) = await RunCli(fixture, ["vault", operation, "--json", "--quiet", "--no-input", "--vault-root", fixture.Root]);
            Assert.Equal(0, exit); Assert.Empty(error);
            using var json = JsonDocument.Parse(output);
            Assert.Equal(1, json.RootElement.GetProperty("schema_version").GetInt32());
            Assert.Equal(operation, json.RootElement.GetProperty("operation").GetString());
            Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal("observed", json.RootElement.GetProperty("metrics").GetProperty("logical_generation_bytes").GetProperty("basis").GetString());
        }
        var cancelled = await RunCli(fixture, ["vault", "verify", "--json"], new CancellationToken(true));
        Assert.Equal(130, cancelled.Exit); using var cancelledJson = JsonDocument.Parse(cancelled.Output);
        Assert.Equal("cancelled", cancelledJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        var invalid = await RunCli(fixture, ["vault", "verify", "--bad", "--json"]);
        Assert.Equal(2, invalid.Exit); using var invalidJson = JsonDocument.Parse(invalid.Output);
        File.Delete(fixture.ManifestPath("g1"));
        var failed = await RunCli(fixture, ["vault", "verify", "--json"]);
        Assert.Equal(1, failed.Exit); Assert.NotEmpty(failed.Error);
        using var failedJson = JsonDocument.Parse(failed.Output);
        Assert.False(failedJson.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal("failure", failedJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, failedJson.RootElement.GetProperty("metrics").ValueKind);
    }

    [Fact]
    public async Task InspectionCompositionDoesNotCreateTheDefaultVaultOrArchive()
    {
        using var fixture = new VaultFixture(); fixture.AddV1("g1");
        var defaultRoot = Path.Combine(fixture.Root, "absent-default");
        var archive = Path.Combine(fixture.Root, "absent.db");
        using var services = new ServiceCollection().AddWeArchiveCore(archive, defaultRoot).BuildServiceProvider();
        var stdout = new StringWriter(); var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(["vault", "verify", "--vault-root", fixture.Root, "--json"], stdout, stderr, services, CancellationToken.None);
        Assert.Equal(0, exit); Assert.False(Directory.Exists(defaultRoot)); Assert.False(File.Exists(archive));
    }

    private static async Task<(int Exit, string Output, string Error)> RunCli(VaultFixture fixture, string[] args, CancellationToken token = default)
    {
        using var services = new ServiceCollection().AddSingleton(new VaultInspectionService(fixture.Root)).BuildServiceProvider();
        var stdout = new StringWriter(); var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(args, stdout, stderr, services, token);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class VaultFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vault-inspection-" + Guid.NewGuid().ToString("N"));
        internal string Account => Path.Combine(Root, "accounts", "account");
        internal byte[] Bytes { get; } = Enumerable.Repeat((byte)42, 8192).ToArray();
        internal string ManifestPath(string id) => Path.Combine(Account, "generations", id, "manifest.json");
        internal Task<VaultInspectionResult> Inspect() => new VaultInspectionService(Root).InspectAsync(true, null, CancellationToken.None);
        internal RawManifest ReadManifest(string id) => RawManifestSerializer.TryDeserialize(File.ReadAllText(ManifestPath(id)))!;
        internal void WriteManifest(string id, RawManifest manifest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(id))!);
            File.WriteAllText(ManifestPath(id), RawManifestSerializer.Serialize(manifest));
        }
        internal void AddV1(string id, string? previous = null, int version = 2)
        {
            var descriptor = new RawArtifactDescriptor { Role = "source", Name = "payload", Size = Bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(), ContentRef = "data" };
            WriteManifest(id, Manifest(id, previous, [descriptor], version, 1));
            File.WriteAllBytes(Path.Combine(Account, "generations", id, "data"), Bytes);
        }
        internal RawVaultV2Format.Artifact AddV2(string id, string? previous = null)
        {
            var artifact = new RawVaultV2PackStore(Account).PutArtifact(Bytes, 4096, CancellationToken.None);
            WriteManifest(id, Manifest(id, previous, [new RawArtifactDescriptor { Role = "source", Name = "payload",
                Size = artifact.Size, Sha256 = artifact.Sha256, Storage = new RawArtifactStorage { Kind = "fixed-block-map-v1",
                    Root = Convert.ToHexString(artifact.Root).ToLowerInvariant(), BlockSize = 4096, BlockCount = artifact.BlockCount } }], 3, 2));
            return artifact;
        }
        private static RawManifest Manifest(string id, string? previous, IReadOnlyList<RawArtifactDescriptor> artifacts, int version, int format) => new()
        {
            ManifestVersion = version, VaultFormatVersion = format, GenerationId = id, AccountId = "account", SourceProfileId = "synthetic",
            PreviousGenerationId = previous, Source = new RawManifestSource { AdapterName = "fixture", AdapterVersion = "1" },
            Capture = new RawManifestCapture { CaptureTime = DateTimeOffset.UnixEpoch, CaptureAdapterFamily = "fixture",
                CaptureAdapterVersion = "1", Mode = RawCaptureMode.Baseline, Completeness = RawGenerationCompleteness.Complete,
                ArtifactCount = artifacts.Count }, Artifacts = artifacts,
        };
        internal string[] Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
