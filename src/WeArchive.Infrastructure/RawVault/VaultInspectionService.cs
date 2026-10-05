using WeArchive.Core.RawVault;

namespace WeArchive.Infrastructure.RawVault;

public sealed record VaultMeasurement(long? Value, string Unit, string Basis);
public sealed record VaultInspectionFailure(string? AccountId, string? GenerationId, string? Artifact, string Message);
public sealed record VaultAccountInspection(string AccountId, string DerivedIndexStatus);
public sealed record VaultInspectionResult(bool Succeeded, string Operation,
    IReadOnlyDictionary<string, VaultMeasurement>? Metrics,
    IReadOnlyList<VaultAccountInspection> Accounts, IReadOnlyList<VaultInspectionFailure> Failures);

/// <summary>
/// Source-neutral, read-only full retained-vault accounting and integrity inspection.
/// Ephemeral pack locations are rebuilt in memory; neither evidence nor derived files are changed.
/// </summary>
public sealed class VaultInspectionService(string vaultRoot)
{
    public Task<VaultInspectionResult> InspectAsync(bool verify, string? rootOverride, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(rootOverride ?? vaultRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Raw Vault '{root}' does not exist.");
        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["account_count"] = 0, ["generation_count"] = 0, ["logical_artifact_references"] = 0,
            ["logical_generation_bytes"] = 0, ["v1_whole_artifact_bytes"] = 0,
            ["v2_unique_payload_uncompressed_bytes"] = 0, ["v2_unique_payload_stored_bytes"] = 0,
            ["v2_reachable_payload_uncompressed_bytes"] = 0, ["v2_orphan_payload_uncompressed_bytes"] = 0,
            ["v2_map_metadata_uncompressed_bytes"] = 0, ["v2_map_metadata_stored_bytes"] = 0,
            ["v2_data_records_stored_bytes"] = 0, ["v2_map_records_stored_bytes"] = 0,
            ["v2_orphan_map_metadata_uncompressed_bytes"] = 0,
            ["v2_pack_logical_bytes"] = 0, ["duplicate_physical_records"] = 0,
            ["duplicate_physical_record_bytes"] = 0, ["derived_lookup_index_bytes"] = 0,
            ["generation_manifest_bytes"] = 0, ["vault_staging_bytes"] = 0,
        };
        long? allocatedBytes = 0;
        var accounts = new List<VaultAccountInspection>();
        var failures = new List<VaultInspectionFailure>();
        string? accountId = null, generationId = null, artifactName = null;
        try
        {
            var accountsRoot = Path.Combine(root, "accounts");
            foreach (var account in Directory.Exists(accountsRoot)
                ? Directory.EnumerateDirectories(accountsRoot).Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
            {
                token.ThrowIfCancellationRequested();
                accountId = Path.GetFileName(account); generationId = null; artifactName = null;
                Add("account_count", 1);
                var inventory = new RawVaultPackInventory(account, token);
                var store = new RawVaultV2PackStore(account, inventory);
                accounts.Add(new(accountId, inventory.IndexStatus(account, token)));
                Add("v2_pack_logical_bytes", inventory.PackBytes);
                allocatedBytes = allocatedBytes is { } total && inventory.AllocatedBytes is { } size ? checked(total + size) : null;
                var unique = inventory.Objects.Values.ToArray();
                Add("v2_unique_payload_uncompressed_bytes", unique.Where(r => r.Kind == 1).Sum(r => (long)r.UncompressedBytes));
                Add("v2_unique_payload_stored_bytes", unique.Where(r => r.Kind == 1).Sum(r => (long)r.StoredBytes));
                Add("v2_map_metadata_uncompressed_bytes", unique.Where(r => r.Kind == 2).Sum(r => (long)r.UncompressedBytes));
                Add("v2_map_metadata_stored_bytes", unique.Where(r => r.Kind == 2).Sum(r => (long)r.StoredBytes));
                Add("v2_data_records_stored_bytes", inventory.Records.Where(r => r.Kind == 1).Sum(r => (long)r.StoredBytes));
                Add("v2_map_records_stored_bytes", inventory.Records.Where(r => r.Kind == 2).Sum(r => (long)r.StoredBytes));
                Add("duplicate_physical_records", inventory.Records.Count - unique.Length);
                Add("duplicate_physical_record_bytes", inventory.Records.Sum(r => (long)r.Length) - unique.Sum(r => (long)r.Length));
                var objectsRoot = Path.Combine(account, "objects");
                if (Directory.Exists(objectsRoot))
                    Add("derived_lookup_index_bytes", Directory.EnumerateFiles(objectsRoot, "lookup.sqlite*").Sum(path => new FileInfo(path).Length));
                var generations = Path.Combine(account, "generations");
                var manifests = new Dictionary<string, RawManifest>(StringComparer.Ordinal);
                var v1Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(generations))
                foreach (var directory in Directory.EnumerateDirectories(generations).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    if (directory.EndsWith(RawVaultStore.StagingSuffix, StringComparison.Ordinal))
                    {
                        Add("vault_staging_bytes", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length));
                        continue;
                    }
                    generationId = Path.GetFileName(directory); artifactName = null;
                    var manifestPath = Path.Combine(directory, "manifest.json");
                    var manifest = RawManifestSerializer.TryDeserialize(File.ReadAllText(manifestPath))
                        ?? throw new InvalidDataException("Invalid or unsupported generation manifest.");
                    if (manifest.GenerationId != generationId || manifest.AccountId != accountId || manifest.Source is null ||
                        manifest.Capture is null || !Enum.IsDefined(manifest.Capture.Completeness) || !Enum.IsDefined(manifest.Capture.Mode) ||
                        manifest.Coverage.Any(c => !Enum.IsDefined(c.Status)))
                        throw new InvalidDataException("Manifest identity or capture semantics are invalid.");
                    manifests.Add(generationId, manifest);
                    Add("generation_count", 1); Add("generation_manifest_bytes", new FileInfo(manifestPath).Length);
                    foreach (var artifact in manifest.Artifacts)
                    {
                        token.ThrowIfCancellationRequested();
                        artifactName = artifact.Name;
                        Add("logical_artifact_references", 1); Add("logical_generation_bytes", artifact.Size);
                        if (manifest.VaultFormatVersion == 1)
                        {
                            // Preserve legacy file/reference rules, including historical SHA validation.
                            using var provider = RawVaultArtifactProvider.Create(new RawGeneration
                            { Manifest = manifest, GenerationDirectory = directory, AccountId = accountId, GenerationId = generationId });
                            provider.VerifyArtifact(artifact, token);
                            var path = Path.GetFullPath(Path.Combine(directory, artifact.ContentRef!));
                            if (v1Paths.Add(path)) Add("v1_whole_artifact_bytes", new FileInfo(path).Length);
                        }
                        else
                        {
                            var storage = artifact.Storage!;
                            store.MaterializeTo(new RawVaultV2Format.Artifact(Convert.FromHexString(storage.Root), artifact.Size,
                                storage.BlockCount, artifact.Sha256, [], storage.BlockSize), Stream.Null, token);
                        }
                    }
                }
                artifactName = null;
                var validatedLineage = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in manifests)
                {
                    generationId = pair.Key;
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    var current = pair.Value;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (validatedLineage.Contains(current.GenerationId)) break;
                        if (!visited.Add(current.GenerationId)) throw new InvalidDataException("Generation lineage contains a cycle.");
                        if (current.PreviousGenerationId is not { } predecessor) break;
                        if (!manifests.TryGetValue(predecessor, out current!))
                            throw new InvalidDataException($"Generation lineage is missing predecessor '{predecessor}'.");
                    }
                    validatedLineage.UnionWith(visited);
                }
                Add("v2_reachable_payload_uncompressed_bytes", unique.Where(r => r.Kind == 1 && inventory.Reachable.Contains(RawVaultPackInventory.Key(r.Kind, r.Digest))).Sum(r => (long)r.UncompressedBytes));
                Add("v2_orphan_payload_uncompressed_bytes", unique.Where(r => r.Kind == 1 && !inventory.Reachable.Contains(RawVaultPackInventory.Key(r.Kind, r.Digest))).Sum(r => (long)r.UncompressedBytes));
                Add("v2_orphan_map_metadata_uncompressed_bytes", unique.Where(r => r.Kind == 2 && !inventory.Reachable.Contains(RawVaultPackInventory.Key(r.Kind, r.Digest))).Sum(r => (long)r.UncompressedBytes));
                var packs = Path.Combine(objectsRoot, "packs");
                if (Directory.Exists(packs)) Add("vault_staging_bytes", Directory.EnumerateFiles(packs, "*.staging").Sum(p => new FileInfo(p).Length));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(new(accountId, generationId, artifactName, ex.Message));
        }
        // A failed scan must never present incomplete amounts as verified totals.
        IReadOnlyDictionary<string, VaultMeasurement>? metrics = null;
        if (failures.Count == 0)
        {
            var measured = counts.ToDictionary(pair => pair.Key, pair => new VaultMeasurement(pair.Value,
                pair.Key.EndsWith("bytes", StringComparison.Ordinal) ? "bytes" : "count", "observed"), StringComparer.Ordinal);
            measured.Add("v2_pack_allocated_bytes", new(allocatedBytes, "bytes", allocatedBytes is null ? "unavailable" : "observed"));
            measured.Add("materialized_cache_bytes", new(null, "bytes", "not_applicable"));
            measured.Add("scratch_peak_bytes", new(null, "bytes", "unavailable"));
            measured.Add("source_changed_page_bytes", new(null, "bytes", "unavailable"));
            measured.Add("storage_changed_block_bytes", new(null, "bytes", "unavailable"));
            measured.Add("storage_write_amplification", new(null, "ratio", "not_applicable"));
            metrics = measured;
        }
        return Task.FromResult(new VaultInspectionResult(failures.Count == 0, verify ? "verify" : "stats", metrics, accounts, failures));

        void Add(string name, long amount) => counts[name] = checked(counts[name] + amount);
    }
}
