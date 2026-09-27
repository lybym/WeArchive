using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Infrastructure.WeChat.Crypto;
using WeArchive.Infrastructure.WeChat.KeyAcquisition;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// The SQLCipher reader is the riskiest piece of the WeChat adapter: a wrong assumption
/// silently produces garbage. These tests build a database in the same encrypted format
/// and prove the reader recovers it exactly, including write-ahead-log recovery.
/// </summary>
public sealed class SqlCipherTests
{
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
    private static readonly byte[] Salt = Convert.FromHexString("bb4baffd819afac9c91221b99218dbf0");

    /// <summary>Creates a small plaintext SQLite database and returns its raw bytes.</summary>
    private static byte[] CreatePlaintextDatabase(string path, int rows = 40)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE messages(id INTEGER PRIMARY KEY, body TEXT); " +
                "CREATE INDEX ix_body ON messages(body);";
            create.ExecuteNonQuery();

            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO messages(id, body) VALUES ($id, $body);";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var body = insert.Parameters.Add("$body", SqliteType.Text);
            for (var i = 1; i <= rows; i++)
            {
                id.Value = i;
                body.Value = new string('x', 200) + i;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            SqliteConnection.ClearAllPools();
        }

        return File.ReadAllBytes(path);
    }

    private static byte[] EncryptPage(byte[] key, byte[] macKey, ReadOnlySpan<byte> plaintext, uint pageNumber, byte[] salt)
    {
        var page = new byte[SqlCipherPageCipher.PageSize];
        var iv = RandomNumberGenerator.GetBytes(16);
        iv.CopyTo(page, SqlCipherPageCipher.IvOffset);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        if (pageNumber == 1)
        {
            salt.CopyTo(page, 0);
            var payload = plaintext.Slice(16, SqlCipherPageCipher.IvOffset - 16).ToArray();
            var cipher = encryptor.TransformFinalBlock(payload, 0, payload.Length);
            cipher.CopyTo(page, 16);
        }
        else
        {
            var payload = plaintext[..SqlCipherPageCipher.IvOffset].ToArray();
            var cipher = encryptor.TransformFinalBlock(payload, 0, payload.Length);
            cipher.CopyTo(page, 0);
        }

        // HMAC over the ciphertext plus IV plus the page number. Page 1 excludes the salt.
        var start = pageNumber == 1 ? 16 : 0;
        var bodyLength = SqlCipherPageCipher.HmacOffset - start;
        var body = new byte[bodyLength + 4];
        page.AsSpan(start, bodyLength).CopyTo(body);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(bodyLength), pageNumber);
        HMACSHA512.HashData(macKey, body).CopyTo(page, SqlCipherPageCipher.HmacOffset);

        return page;
    }

    private static byte[] EncryptDatabase(byte[] plaintext, byte[] key, byte[] salt)
    {
        var macKey = SqlCipherPageCipher.DeriveMacKey(key, salt);
        var pageCount = plaintext.Length / SqlCipherPageCipher.PageSize;
        var encrypted = new byte[plaintext.Length];

        for (var i = 0; i < pageCount; i++)
        {
            var page = EncryptPage(
                key,
                macKey,
                plaintext.AsSpan(i * SqlCipherPageCipher.PageSize, SqlCipherPageCipher.PageSize),
                (uint)(i + 1),
                salt);
            page.CopyTo(encrypted, i * SqlCipherPageCipher.PageSize);
        }

        return encrypted;
    }

    [Fact]
    public void PageVerificationAcceptsTheRightKeyAndRejectsOthers()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"));
        var encrypted = EncryptDatabase(plaintext, Key, Salt);
        var page1 = encrypted.AsSpan(0, SqlCipherPageCipher.PageSize).ToArray();

        var goodMac = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        Assert.True(SqlCipherPageCipher.VerifyPage(goodMac, page1, 1));

        var wrongKey = new byte[32];
        Key.CopyTo(wrongKey, 0);
        wrongKey[0] ^= 0xFF;
        Assert.False(SqlCipherPageCipher.VerifyPage(SqlCipherPageCipher.DeriveMacKey(wrongKey, Salt), page1, 1));
    }

    [Fact]
    public void DecryptedPagesReproduceTheOriginalPagePayload()
    {
        // A plain SQLite database produced by this test has no reserved area, whereas
        // SQLCipher reserves the final 80 bytes of every page for the IV and HMAC. Only the
        // usable region is therefore compared; the reserved tail is intentionally zeroed.
        var plainPage1 = new byte[SqlCipherPageCipher.PageSize];
        "SQLite format 3\0"u8.CopyTo(plainPage1);
        RandomNumberGenerator.Fill(plainPage1.AsSpan(16, SqlCipherPageCipher.IvOffset - 16));

        var macKey = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        var encrypted1 = EncryptPage(Key, macKey, plainPage1, 1, Salt);
        var decrypted1 = SqlCipherPageCipher.DecryptPage(Key, encrypted1, 1);
        Assert.Equal(
            plainPage1.AsSpan(0, SqlCipherPageCipher.HmacOffset).ToArray(),
            decrypted1.AsSpan(0, SqlCipherPageCipher.HmacOffset).ToArray());

        var plainPage7 = new byte[SqlCipherPageCipher.PageSize];
        RandomNumberGenerator.Fill(plainPage7.AsSpan(0, SqlCipherPageCipher.IvOffset));
        var encrypted7 = EncryptPage(Key, macKey, plainPage7, 7, Salt);
        var decrypted7 = SqlCipherPageCipher.DecryptPage(Key, encrypted7, 7);
        Assert.Equal(
            plainPage7.AsSpan(0, SqlCipherPageCipher.IvOffset).ToArray(),
            decrypted7.AsSpan(0, SqlCipherPageCipher.IvOffset).ToArray());
    }

    [Fact]
    public void ReaderMaterializesAPageAccurateImageAndLeavesTheSourceUntouched()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 60);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));
        var originalEncrypted = File.ReadAllBytes(encryptedPath);

        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);

        var outcome = cache.GetPlaintext(encryptedPath);
        Assert.False(outcome.WasPlaintext);
        Assert.Equal(0, outcome.WalFramesApplied);

        var image = File.ReadAllBytes(outcome.PlaintextPath);
        Assert.Equal(plaintext.Length, image.Length);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(image, 0, 16));

        for (var i = 0; i < image.Length / SqlCipherPageCipher.PageSize; i++)
        {
            var offset = i * SqlCipherPageCipher.PageSize;

            // Page 1 keeps its decrypted 16-byte header; every page compares byte for byte
            // over the usable region.
            var usableStart = i == 0 ? 16 : 0;
            Assert.Equal(
                plaintext.AsSpan(offset + usableStart, SqlCipherPageCipher.IvOffset - usableStart).ToArray(),
                image.AsSpan(offset + usableStart, SqlCipherPageCipher.IvOffset - usableStart).ToArray());
        }

        // The source database must never be modified by a read.
        Assert.Equal(originalEncrypted, File.ReadAllBytes(encryptedPath));
    }

    [Fact]
    public void ReaderAppliesOnlyCommittedFramesFromTheCurrentWalGeneration()
    {
        using var temp = new TempDirectory();
        var plaintextPath = temp.Combine("plain.db");
        var plaintext = CreatePlaintextDatabase(plaintextPath, rows: 40);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));

        var macKey = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        const uint salt1 = 0x32D1F04A;
        const uint salt2 = 0x3824DAB3;
        const byte marker = 0x41;

        // Build a WAL carrying: one committed update to page 2, a commit marker, then a frame
        // from a previous WAL generation that must be ignored.
        var wal = new List<byte>();
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0x377F0682);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 3007000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), SqlCipherPageCipher.PageSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), salt2);
        wal.AddRange(header);

        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 1, marker), 2, Salt),
            pageNumber: 2,
            databaseSize: (uint)(plaintext.Length / SqlCipherPageCipher.PageSize),
            frameSalt1: salt1,
            frameSalt2: salt2));
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 3, marker), 4, Salt),
            pageNumber: 4,
            databaseSize: 0,
            frameSalt1: salt1 - 1,
            frameSalt2: salt2));

        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var outcome = cache.GetPlaintext(encryptedPath);

        Assert.Equal(1, outcome.WalFramesApplied);
        Assert.Equal(0, outcome.WalFramesRejected);

        var image = File.ReadAllBytes(outcome.PlaintextPath);

        // Page 2 carries the committed frame...
        Assert.Equal(marker, image[SqlCipherPageCipher.PageSize]);
        // ...while page 4 still holds its original content from the main database.
        var originalPage4 = plaintext.AsSpan(3 * SqlCipherPageCipher.PageSize, SqlCipherPageCipher.PageSize);
        Assert.NotEqual(marker, image[3 * SqlCipherPageCipher.PageSize]);
        Assert.Equal(
            originalPage4[..64].ToArray(),
            image.AsSpan(3 * SqlCipherPageCipher.PageSize, 64).ToArray());
    }

    private static byte[] MarkedPage(byte[] database, int pageIndex, byte marker)
    {
        var page = database.AsSpan(pageIndex * SqlCipherPageCipher.PageSize, SqlCipherPageCipher.PageSize).ToArray();
        for (var i = 0; i < 64; i++)
        {
            page[i] = marker;
        }

        return page;
    }

    [Fact]
    public void ReaderLeavesPlaintextDatabasesAlone()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("contact_fts.db");
        CreatePlaintextDatabase(path, rows: 5);

        var keys = new WeChatKeySet([]);
        using var cache = new SqlCipherDatabaseCache(keys);

        var outcome = cache.GetPlaintext(path);
        Assert.True(outcome.WasPlaintext);
        Assert.Equal(path, outcome.PlaintextPath);
    }

    [Fact]
    public void MaterializingAPlaintextImageAgainNeverDeletesIt()
    {
        using var temp = new TempDirectory();
        var artifact = temp.Combine("biz_message_0.db");
        CreatePlaintextDatabase(artifact, rows: 5);

        var keys = new WeChatKeySet([]);
        using var cache = new SqlCipherDatabaseCache(keys);

        var first = cache.GetPlaintext(artifact);
        Assert.True(first.WasPlaintext);
        Assert.Equal(artifact, first.PlaintextPath);

        // SQLite opening a preserved WAL-mode image in place creates -wal/-shm sidecars next to it,
        // which changes the cache fingerprint and forces a re-materialization. The caller's own
        // file must never be treated as scratch: doing so deleted a published Raw Vault artifact
        // during a real rebuild, which is the immutability violation Issue #37 exposed.
        File.WriteAllBytes(artifact + "-wal", [0, 0, 0, 0]);

        var second = cache.GetPlaintext(artifact);

        Assert.True(File.Exists(artifact));
        Assert.Equal(artifact, second.PlaintextPath);
        Assert.True(second.WasPlaintext);
        Assert.Equal(5, SqliteConnectionPooledRowCount(artifact));
    }

    [Fact]
    public void MaterializingAnEncryptedDatabaseAgainReplacesOnlyItsScratchImage()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"));
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));

        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);

        var first = cache.GetPlaintext(encryptedPath);
        Assert.False(first.WasPlaintext);
        Assert.True(File.Exists(first.PlaintextPath));

        // The fingerprint includes the source write time, so touching the source re-materializes
        // and the cache's own previous scratch image is still cleaned up.
        File.SetLastWriteTimeUtc(encryptedPath, DateTime.UtcNow.AddMinutes(1));
        var second = cache.GetPlaintext(encryptedPath);

        Assert.NotEqual(first.PlaintextPath, second.PlaintextPath);
        Assert.False(File.Exists(first.PlaintextPath));
        Assert.True(File.Exists(second.PlaintextPath));
        Assert.True(File.Exists(encryptedPath));
    }

    private static long SqliteConnectionPooledRowCount(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages;";
        var count = (long)command.ExecuteScalar()!;
        SqliteConnection.ClearAllPools();
        return count;
    }

    [Fact]
    public void OpeningAPreservedImageReadOnlyCreatesNoSidecarsAndKeepsTheImage()
    {
        using var temp = new TempDirectory();
        var generationArtifacts = temp.Combine("generation", "artifacts");
        Directory.CreateDirectory(generationArtifacts);
        var artifact = Path.Combine(generationArtifacts, "artifact.db");
        CreateWriteAheadLogModeDatabase(artifact, rows: 5);
        var before = File.ReadAllBytes(artifact);

        using var cache = SqlCipherDatabaseCache.ForCapturedPlaintext();
        using (var connection = cache.OpenReadOnly(artifact))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM messages;";
            Assert.Equal(5L, command.ExecuteScalar());
        }

        // Issue #37: reading preserved evidence must not mutate the published generation it came
        // from. A preserved image is immutable evidence, so SQLite must not create -wal/-shm
        // sidecars next to it (which also used to delete the artifact via the fingerprint change).
        Assert.False(File.Exists(artifact + "-wal"));
        Assert.False(File.Exists(artifact + "-shm"));
        Assert.Equal(before, File.ReadAllBytes(artifact));
        Assert.Equal(
            new[] { "artifact.db" },
            Directory.EnumerateFileSystemEntries(generationArtifacts).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void TheSourceCacheStillReadsWriteAheadLogCommittedFrames()
    {
        // Immutable semantics are reserved for preserved images. The live/capture cache must keep
        // reading committed WAL frames, because a live source database's current state can live
        // only in its write-ahead log.
        using var temp = new TempDirectory();
        var pair = CreateWriteAheadLogOnlyDatabasePair(temp.Path);
        Assert.True(new FileInfo(pair + "-wal").Length > 32);

        var keys = new WeChatKeySet([]);
        using var cache = new SqlCipherDatabaseCache(keys);
        using var connection = cache.OpenReadOnly(pair);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages;";

        Assert.Equal(5L, command.ExecuteScalar());
    }

    /// <summary>Creates a plaintext database whose header is in WAL mode, then closes it cleanly.</summary>
    private static void CreateWriteAheadLogModeDatabase(string path, int rows)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TABLE messages(id INTEGER PRIMARY KEY, body TEXT);";
        command.ExecuteNonQuery();
        for (var i = 1; i <= rows; i++)
        {
            command.CommandText = $"INSERT INTO messages VALUES ({i}, '{new string('x', 50)}');";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Copies a WAL-mode database while its connection is open, so the committed rows exist only in
    /// the copied <c>-wal</c> file, exactly like a live WeChat database mid-session.
    /// </summary>
    private static string CreateWriteAheadLogOnlyDatabasePair(string directory)
    {
        var source = Path.Combine(directory, "source.db");
        var pair = Path.Combine(directory, "pair.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE TABLE messages(id INTEGER PRIMARY KEY, body TEXT);";
            command.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            for (var i = 1; i <= 5; i++)
            {
                command.CommandText = $"INSERT INTO messages VALUES ({i}, 'body-{i}');";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            command.Transaction = null;

            // Copy while the connection still holds the WAL, so the frames are not checkpointed.
            File.Copy(source, pair, overwrite: true);
            File.Copy(source + "-wal", pair + "-wal", overwrite: true);
        }

        SqliteConnection.ClearAllPools();
        return pair;
    }

    [Fact]
    public void ReaderFailsClosedWhenNoKeyMatches()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"));
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));

        var wrongKey = new byte[32];
        Key.CopyTo(wrongKey, 0);
        wrongKey[5] ^= 0x5A;

        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), wrongKey)]);
        using var cache = new SqlCipherDatabaseCache(keys);

        Assert.Throws<WeChatKeyUnavailableException>(() => cache.GetPlaintext(encryptedPath));
    }

    private static byte[] BuildFrame(byte[] encryptedPage, uint pageNumber, uint databaseSize, uint frameSalt1, uint frameSalt2)
    {
        var frame = new byte[24 + SqlCipherPageCipher.PageSize];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), pageNumber);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), databaseSize);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), frameSalt1);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12, 4), frameSalt2);
        encryptedPage.CopyTo(frame, 24);
        return frame;
    }
}
