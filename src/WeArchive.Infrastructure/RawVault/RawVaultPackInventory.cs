using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace WeArchive.Infrastructure.RawVault;

/// <summary>Ephemeral locations obtained solely from validated sealed packs; no persistent writes.</summary>
internal sealed class RawVaultPackInventory
{
    internal sealed record Record(string File, long Offset, int Length, byte Kind, byte[] Digest,
        int UncompressedBytes, int StoredBytes);
    internal Dictionary<string, Record> Objects { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Reachable { get; } = new(StringComparer.Ordinal);
    internal List<Record> Records { get; } = [];
    internal long PackBytes { get; private set; }
    internal long? AllocatedBytes { get; private set; } = 0;

    internal RawVaultPackInventory(string account, CancellationToken token)
    {
        var packs = Path.Combine(account, "objects", "packs");
        if (!Directory.Exists(packs)) return;
        foreach (var path in Directory.EnumerateFiles(packs, "*.rvpk").Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = File.ReadAllBytes(path);
                var values = RawVaultV2Format.ReadPack(bytes);
                PackBytes = checked(PackBytes + bytes.LongLength);
                var allocated = FileAllocation.Measure(path);
                AllocatedBytes = AllocatedBytes is { } total && allocated is { } size ? checked(total + size) : null;
                var offset = 16;
                foreach (var value in values)
                {
                    token.ThrowIfCancellationRequested();
                    var stored = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12)));
                    var record = new Record(path, offset, checked(48 + stored), value.Kind, value.Digest, value.Bytes.Length, stored);
                    Records.Add(record);
                    Objects.TryAdd(Key(value.Kind, value.Digest), record);
                    offset = checked(offset + record.Length);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"Pack '{path}' failed validation: {ex.Message}", ex);
            }
        }
    }

    internal IReadOnlyList<RawVaultV2Format.StoredObject> ReadMany(byte kind, IReadOnlyList<byte[]> digests)
    {
        var streams = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        var values = new List<RawVaultV2Format.StoredObject>(digests.Count);
        try
        {
            foreach (var digest in digests)
            {
                var key = Key(kind, digest);
                if (!Objects.TryGetValue(key, out var record))
                    throw new InvalidDataException($"Missing authoritative object '{key}'.");
                if (!streams.TryGetValue(record.File, out var input))
                {
                    input = new FileStream(record.File, FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams.Add(record.File, input);
                }
                input.Position = record.Offset;
                var bytes = new byte[record.Length]; input.ReadExactly(bytes);
                var value = RawVaultV2Format.DecodeRecord(bytes);
                if (Key(value.Kind, value.Digest) != key)
                    throw new InvalidDataException($"Authoritative object '{key}' changed during inspection.");
                Reachable.Add(key); values.Add(value);
            }
            return values;
        }
        finally { foreach (var input in streams.Values) input.Dispose(); }
    }

    internal string IndexStatus(string account, CancellationToken token)
    {
        var path = Path.Combine(account, "objects", "lookup.sqlite");
        if (!File.Exists(path)) return Objects.Count == 0 ? "not_applicable" : "missing_rebuildable";
        // The supported lookup index uses rollback journaling. Do not open a WAL-mode index:
        // SQLite read-only WAL connections may create/write shared-memory sidecars.
        if (File.Exists(path + "-wal")) return "inconsistent_rebuildable";
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal)) return "corrupt_rebuildable";
            command.CommandText = "SELECT kind,digest,pack_file,record_offset,record_length FROM objects";
            using var reader = command.ExecuteReader();
            var found = new HashSet<string>(StringComparer.Ordinal);
            var locations = Records.Select(r => (Key(r.Kind, r.Digest), Path.GetFileName(r.File), r.Offset, r.Length)).ToHashSet();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var key = Key(reader.GetByte(0), (byte[])reader[1]);
                if (!found.Add(key) || !locations.Contains((key, reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4))))
                    return "inconsistent_rebuildable";
            }
            return found.SetEquals(Objects.Keys) ? "consistent" : "inconsistent_rebuildable";
        }
        catch (Exception ex) when (ex is SqliteException or InvalidCastException or IOException)
        { return "corrupt_rebuildable"; }
    }

    internal static string Key(byte kind, byte[] digest) => kind + ":" + Convert.ToHexString(digest);
}

internal static class FileAllocation
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        int informationClass, out FileStandardInfo information, uint size);

    internal static long? Measure(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GetFileInformationByHandleEx(file, 1, out var information,
            checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<FileStandardInfo>())) && information.AllocationSize >= 0
            ? information.AllocationSize : null;
    }
}
