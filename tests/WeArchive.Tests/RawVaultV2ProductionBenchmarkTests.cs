using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeArchive.Infrastructure.RawVault;
using Xunit.Abstractions;

namespace WeArchive.Tests;

public sealed class RawVaultV2ProductionBenchmarkTests(ITestOutputHelper output)
{
    [RawVaultV2ProductionBenchmarkFact]
    public void MeasuresProductionShapedVaultWithTheActualPackAndIndexEngine()
    {
        var sourceRoot = Environment.GetEnvironmentVariable("WEARCHIVE_RAW_VAULT_BENCHMARK_ROOT")!;
        var accountDirectories = Directory.GetDirectories(Path.Combine(sourceRoot, "accounts"));
        Assert.Single(accountDirectories);
        var accountDirectory = accountDirectories[0];
        var generationsRoot = Path.Combine(accountDirectory, "generations");
        var generations = Directory.GetFiles(generationsRoot, "manifest.json", SearchOption.AllDirectories)
            .Select(path => ReadGeneration(Path.GetDirectoryName(path)!, path))
            .OrderBy(generation => generation.CaptureTime, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(8, generations.Length);
        Assert.All(generations, generation => Assert.Equal("complete", generation.Completeness));
        Assert.All(generations, generation => Assert.Equal(33, generation.Artifacts.Count));

        using var inputFingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var generation in generations)
            foreach (var artifact in generation.Artifacts)
                inputFingerprint.AppendData(Convert.FromHexString(artifact.Sha256));
        var workloadSha256 = Convert.ToHexString(inputFingerprint.GetHashAndReset()).ToLowerInvariant();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "wearchive-v2-production-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            foreach (var blockSize in RawVaultV2Format.SupportedBlockSizes)
            {
                foreach (var compress in new[] { false, true })
                {
                    var candidateRoot = Path.Combine(temporaryRoot, $"{blockSize}-{(compress ? "zstd1" : "none")}");
                    Directory.CreateDirectory(candidateRoot);
                    var store = new RawVaultV2PackStore(candidateRoot, compress);
                    var generationStats = new List<(long PackAllocated, long IndexAllocated, long DataObjects)>();
                    var candidateDescriptors = new List<RawVaultV2Format.Artifact>();
                    var engineCpu = Process.GetCurrentProcess().TotalProcessorTime;
                    var writerClock = Stopwatch.StartNew();

                    foreach (var generation in generations)
                    {
                        foreach (var source in generation.Artifacts)
                        {
                            using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 128 * 1024);
                            Assert.Equal(source.Size, input.Length);
                            var descriptor = store.PutArtifact(input, source.Size, blockSize, CancellationToken.None);
                            Assert.Equal(source.Sha256, descriptor.Sha256);
                            candidateDescriptors.Add(new RawVaultV2Format.Artifact(
                                descriptor.Root, descriptor.Size, descriptor.BlockCount, descriptor.Sha256, [], descriptor.BlockSize));
                        }

                        var stats = ReadIndexAndAllocationStats(candidateRoot);
                        generationStats.Add(stats);
                    }

                    writerClock.Stop();
                    var cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - engineCpu).TotalSeconds;
                    var finalSummary = ScanPacks(candidateRoot);
                    var baseline = checked(generationStats[0].PackAllocated + generationStats[0].IndexAllocated);
                    var incremental = new List<long>();
                    for (var i = 1; i < generationStats.Count; i++)
                    {
                        var before = checked(generationStats[i - 1].PackAllocated + generationStats[i - 1].IndexAllocated);
                        var after = checked(generationStats[i].PackAllocated + generationStats[i].IndexAllocated);
                        incremental.Add(checked(after - before));
                    }
                    var meanIncremental = incremental.Average(value => (double)value);
                    var t365 = (baseline + 8760d * meanIncremental) / 1_000_000_000d;

                    var latest = candidateDescriptors.TakeLast(33).ToArray();
                    var materialization = MeasureMaterialization(store, latest);
                    var historical = SelectHistorical(candidateDescriptors, 24);
                    var historicalMaterialization = MeasureMaterialization(store, historical);

                    output.WriteLine(
                        $"block={blockSize} codec={(compress ? "zstd1/raw-fallback" : "none")} " +
                        $"baseline_allocated_bytes={baseline} mean_incremental_bytes={meanIncremental:F0} " +
                        $"T365_GB={t365:F3} total_pack_allocated_bytes={finalSummary.PackAllocated} " +
                        $"index_allocated_bytes={finalSummary.IndexAllocated} data_objects={finalSummary.DataObjects} " +
                        $"map_objects={finalSummary.MapObjects} pack_count={finalSummary.PackCount} " +
                        $"record_header_bytes={finalSummary.RecordHeaderBytes} pack_framing_bytes={finalSummary.PackFramingBytes} " +
                        $"unique_data_raw_bytes={finalSummary.UniqueDataRawBytes} " +
                        $"unique_data_stored_bytes={finalSummary.UniqueDataStoredBytes} map_stored_bytes={finalSummary.MapStoredBytes} " +
                        $"engine_cpu_seconds={cpuSeconds:F2} engine_wall_seconds={writerClock.Elapsed.TotalSeconds:F2} " +
                        $"latest_materialize_mib_s={materialization.MebibytesPerSecond:F2} " +
                        $"historical24_materialize_mib_s={historicalMaterialization.MebibytesPerSecond:F2}");
                }
            }

            output.WriteLine($"workload_sha256={workloadSha256} generations={generations.Length} artifacts={generations.Sum(g => g.Artifacts.Count)}");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static Generation ReadGeneration(string directory, string manifestPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = json.RootElement;
        var generationId = root.GetProperty("generation_id").GetString()!;
        var captureTime = root.GetProperty("capture").GetProperty("capture_time").GetString()!;
        var completeness = root.GetProperty("capture").GetProperty("completeness").GetString()!;
        var artifacts = root.GetProperty("artifacts").EnumerateArray().Select(element =>
        {
            var relativePath = element.GetProperty("content_ref").GetString()!;
            var path = Path.GetFullPath(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The benchmark source artifact path escapes its generation directory.");
            return new SourceArtifact(path, element.GetProperty("size").GetInt64(), element.GetProperty("sha256").GetString()!);
        }).ToArray();
        return new Generation(generationId, captureTime, completeness, artifacts);
    }

    private static (long PackAllocated, long IndexAllocated, long DataObjects) ReadIndexAndAllocationStats(string root)
    {
        var packAllocated = Directory.GetFiles(Path.Combine(root, "objects", "packs"), "*.rvpk")
            .Sum(path => checked((long)GetAllocatedSize(path)));
        var indexPath = Path.Combine(root, "objects", "lookup.sqlite");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = indexPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM objects WHERE kind=1";
        return (packAllocated, checked((long)GetAllocatedSize(indexPath)), Convert.ToInt64(command.ExecuteScalar()));
    }

    private static PackSummary ScanPacks(string root)
    {
        long allocated = 0, rawData = 0, storedData = 0, storedMaps = 0, dataObjects = 0, mapObjects = 0, packCount = 0;
        Span<byte> footer = stackalloc byte[56];
        Span<byte> header = stackalloc byte[48];
        foreach (var path in Directory.GetFiles(Path.Combine(root, "objects", "packs"), "*.rvpk"))
        {
            packCount++;
            allocated = checked(allocated + (long)GetAllocatedSize(path));
            using var stream = File.OpenRead(path);
            if (stream.Length < 72) throw new InvalidDataException("Truncated benchmark pack.");
            stream.Position = stream.Length - 56;
            stream.ReadExactly(footer);
            var contentLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(footer[16..]));
            stream.Position = 16;
            long records = 0;
            while (stream.Position < contentLength)
            {
                stream.ReadExactly(header);
                var kind = header[5]; var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                var storedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
                if (kind == 1) { dataObjects++; rawData += rawLength; storedData += storedLength; }
                else if (kind == 2) mapObjects++;
                else throw new InvalidDataException("Unsupported object kind in benchmark pack.");
                storedMaps += kind == 2 ? storedLength : 0;
                stream.Seek(storedLength, SeekOrigin.Current);
                records++;
            }
            if (records != checked((long)BinaryPrimitives.ReadUInt64LittleEndian(footer[8..]))) throw new InvalidDataException("Benchmark pack record count mismatch.");
        }
        var indexPath = Path.Combine(root, "objects", "lookup.sqlite");
        var indexAllocated = checked((long)GetAllocatedSize(indexPath));
        return new PackSummary(allocated, indexAllocated, dataObjects, mapObjects, rawData, storedData, storedMaps,
            packCount, checked((dataObjects + mapObjects) * 48), checked(packCount * 72));
    }

    private static (double MebibytesPerSecond, long Bytes) MeasureMaterialization(
        RawVaultV2PackStore store, IReadOnlyList<RawVaultV2Format.Artifact> artifacts)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), "wearchive-v2-materialized-" + Guid.NewGuid().ToString("N"));
        long bytes = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var artifact in artifacts)
            {
                using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                store.MaterializeTo(artifact, output, CancellationToken.None);
                output.Flush();
                bytes = checked(bytes + output.Length);
            }
            stopwatch.Stop();
            return (bytes / (1024d * 1024d) / stopwatch.Elapsed.TotalSeconds, bytes);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static IReadOnlyList<RawVaultV2Format.Artifact> SelectHistorical(
        IReadOnlyList<RawVaultV2Format.Artifact> artifacts, int count)
    {
        var indices = Enumerable.Range(0, artifacts.Count).Where(i => i < artifacts.Count - 33).Distinct().ToArray();
        var random = new Random(7919);
        return indices.OrderBy(_ => random.Next()).Take(Math.Min(count, indices.Length)).Select(i => artifacts[i]).ToArray();
    }

    private static ulong GetAllocatedSize(string path)
    {
        var low = NativeFileSize.GetCompressedFileSize(path, out var high);
        var error = Marshal.GetLastWin32Error();
        if (low == uint.MaxValue && error != 0) throw new IOException("Could not read filesystem allocation size.", new Win32Exception(error));
        return ((ulong)high << 32) | low;
    }

    private sealed record Generation(string Id, string CaptureTime, string Completeness, IReadOnlyList<SourceArtifact> Artifacts);
    private sealed record SourceArtifact(string Path, long Size, string Sha256);
    private sealed record PackSummary(long PackAllocated, long IndexAllocated, long DataObjects, long MapObjects,
        long UniqueDataRawBytes, long UniqueDataStoredBytes, long MapStoredBytes, long PackCount,
        long RecordHeaderBytes, long PackFramingBytes);

    private static class NativeFileSize
    {
#pragma warning disable SYSLIB1054
        [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
#pragma warning restore SYSLIB1054
    }
}

internal sealed class RawVaultV2ProductionBenchmarkFactAttribute : FactAttribute
{
    public RawVaultV2ProductionBenchmarkFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable("WEARCHIVE_RAW_VAULT_BENCHMARK_ROOT");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            Skip = "Set WEARCHIVE_RAW_VAULT_BENCHMARK_ROOT to a local Raw Vault root to run the privacy-safe aggregate benchmark.";
    }
}
