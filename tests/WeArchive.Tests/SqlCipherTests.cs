using System.Buffers.Binary;
using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderIgnoresAStaleGenerationTailAfterTheCurrentWalFrames(bool checksumBigEndian)
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

        // Build a reused WAL carrying a committed update to page 2, then a full stale-generation
        // frame and a short physical tail. The stale salt is the logical end of the current WAL;
        // old bytes after it must not downgrade the committed snapshot.
        var wal = CreateWal(salt1, salt2, checksumBigEndian);

        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 1, marker), 2, Salt),
            pageNumber: 2,
            databaseSize: 0,
            frameSalt1: salt1,
            frameSalt2: salt2));
        const byte repeatedPageMarker = 0x42;
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 1, repeatedPageMarker), 2, Salt),
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
        wal.AddRange([0x7F, 0x41, 0x20]); // leftover bytes after the stale-generation boundary

        SealWalChecksums(wal);

        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var outcome = cache.GetPlaintext(encryptedPath);

        Assert.Equal(2, outcome.WalFramesApplied);
        Assert.Equal(0, outcome.WalFramesRejected);
        Assert.Equal(plaintext.Length / SqlCipherPageCipher.PageSize, outcome.PageCount);

        var image = File.ReadAllBytes(outcome.PlaintextPath);

        // The repeated page is applied in frame order, and the last committed copy wins...
        Assert.Equal(repeatedPageMarker, image[SqlCipherPageCipher.PageSize]);
        // ...while page 4 still holds its original content from the main database.
        var originalPage4 = plaintext.AsSpan(3 * SqlCipherPageCipher.PageSize, SqlCipherPageCipher.PageSize);
        Assert.NotEqual(marker, image[3 * SqlCipherPageCipher.PageSize]);
        Assert.Equal(
            originalPage4[..64].ToArray(),
            image.AsSpan(3 * SqlCipherPageCipher.PageSize, 64).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderKeepsACurrentGenerationShortWalTailPartial(bool checksumBigEndian)
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 40);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var baseline = cache.GetPlaintext(encryptedPath);
        var expected = File.ReadAllBytes(baseline.PlaintextPath);

        var macKey = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        const uint salt1 = 0x32D1F04A;
        const uint salt2 = 0x3824DAB3;
        const byte marker = 0x47;
        var wal = CreateWal(salt1, salt2, checksumBigEndian);
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 1, marker), 2, Salt),
            2,
            (uint)(plaintext.Length / SqlCipherPageCipher.PageSize),
            salt1,
            salt2));
        var partialCurrentFrame = BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 2, marker: 0x48), 3, Salt),
            3,
            0,
            salt1,
            salt2);
        wal.AddRange(partialCurrentFrame.AsSpan(0, 40).ToArray()); // includes the current salts, but not a full frame
        SealWalChecksums(wal);
        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var outcome = cache.GetPlaintext(encryptedPath);
        expected.AsSpan(SqlCipherPageCipher.PageSize, 64).Fill(marker);

        Assert.Equal(1, outcome.WalFramesApplied);
        Assert.Equal(1, outcome.WalFramesRejected);
        Assert.Equal(plaintext.Length / SqlCipherPageCipher.PageSize, outcome.PageCount);
        Assert.Equal(expected, File.ReadAllBytes(outcome.PlaintextPath));
    }

    [Fact]
    public void ReaderRejectsAWalHeaderShorterThanItsFixedHeader()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 40);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var baseline = cache.GetPlaintext(encryptedPath);
        var expected = File.ReadAllBytes(baseline.PlaintextPath);

        var wal = CreateWal(0x32D1F04A, 0x3824DAB3);
        wal.RemoveRange(31, wal.Count - 31);
        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var outcome = cache.GetPlaintext(encryptedPath);

        Assert.Equal(0, outcome.WalFramesApplied);
        Assert.Equal(1, outcome.WalFramesRejected);
        Assert.Equal(plaintext.Length / SqlCipherPageCipher.PageSize, outcome.PageCount);
        Assert.Equal(expected, File.ReadAllBytes(outcome.PlaintextPath));
    }

    [Fact]
    public void ReaderRejectsBadWalHeaderChecksumWithoutChangingMaterializedBytes()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 40);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var baseline = cache.GetPlaintext(encryptedPath);
        var expected = File.ReadAllBytes(baseline.PlaintextPath);

        var wal = CreateWal(0x32D1F04A, 0x3824DAB3);
        CollectionsMarshal.AsSpan(wal)[24] ^= 0x01;
        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var outcome = cache.GetPlaintext(encryptedPath);

        Assert.Equal(0, outcome.WalFramesApplied);
        Assert.Equal(1, outcome.WalFramesRejected);
        Assert.Equal(plaintext.Length / SqlCipherPageCipher.PageSize, outcome.PageCount);
        Assert.Equal(expected, File.ReadAllBytes(outcome.PlaintextPath));
    }

    [Fact]
    public void ReaderRejectsBadFrameChecksumAfterKeepingEarlierCommittedState()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 40);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var baseline = cache.GetPlaintext(encryptedPath);
        var expected = File.ReadAllBytes(baseline.PlaintextPath);

        var macKey = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        const uint salt1 = 0x32D1F04A;
        const uint salt2 = 0x3824DAB3;
        var wal = CreateWal(salt1, salt2);
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 1, marker: 0x41), 2, Salt),
            2, (uint)(plaintext.Length / SqlCipherPageCipher.PageSize), salt1, salt2));
        var invalidFrameOffset = wal.Count;
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 2, marker: 0x42), 3, Salt),
            3, 0, salt1, salt2));
        SealWalChecksums(wal);
        CollectionsMarshal.AsSpan(wal)[invalidFrameOffset + 16] ^= 0x01;
        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var outcome = cache.GetPlaintext(encryptedPath);
        expected.AsSpan(SqlCipherPageCipher.PageSize, 64).Fill(0x41);

        Assert.Equal(1, outcome.WalFramesApplied);
        Assert.Equal(1, outcome.WalFramesRejected);
        Assert.Equal(plaintext.Length / SqlCipherPageCipher.PageSize, outcome.PageCount);
        Assert.Equal(expected, File.ReadAllBytes(outcome.PlaintextPath));
    }

    [Fact]
    public void ReaderUsesCheckpointedMainImageAfterWalResetLeavesOnlyOldSaltFrames()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 40);
        var checkpointed = plaintext.ToArray();
        checkpointed.AsSpan(SqlCipherPageCipher.PageSize, 64).Fill(0x43);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(checkpointed, Key, Salt));
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var baseline = cache.GetPlaintext(encryptedPath);
        var expected = File.ReadAllBytes(baseline.PlaintextPath);

        const uint resetSalt1 = 0x19C2A4E7;
        const uint resetSalt2 = 0x57B8D30F;
        var macKey = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        var wal = CreateWal(resetSalt1, resetSalt2);
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 1, marker: 0x44), 2, Salt),
            2, (uint)(plaintext.Length / SqlCipherPageCipher.PageSize), resetSalt1 - 1, resetSalt2));
        SealWalChecksums(wal);
        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var outcome = cache.GetPlaintext(encryptedPath);

        Assert.Equal(0, outcome.WalFramesApplied);
        Assert.Equal(0, outcome.WalFramesRejected);
        Assert.Equal(plaintext.Length / SqlCipherPageCipher.PageSize, outcome.PageCount);
        Assert.Equal(expected, File.ReadAllBytes(outcome.PlaintextPath));
    }

    [Fact]
    public void ReaderAppliesEachCommittedTransactionIncludingTruncate()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 40);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);
        var baseline = cache.GetPlaintext(encryptedPath);
        var original = File.ReadAllBytes(baseline.PlaintextPath);

        var macKey = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        const uint salt1 = 0x32D1F04A;
        const uint salt2 = 0x3824DAB3;
        var wal = CreateWal(salt1, salt2);
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 3, marker: 0x45), 4, Salt),
            4, 4, salt1, salt2));
        wal.AddRange(BuildFrame(
            EncryptPage(Key, macKey, MarkedPage(plaintext, pageIndex: 1, marker: 0x46), 2, Salt),
            2, 2, salt1, salt2));
        SealWalChecksums(wal);
        File.WriteAllBytes(encryptedPath + "-wal", [.. wal]);

        var outcome = cache.GetPlaintext(encryptedPath);
        var expected = original[..(2 * SqlCipherPageCipher.PageSize)];
        expected.AsSpan(SqlCipherPageCipher.PageSize, 64).Fill(0x46);

        Assert.Equal(2, outcome.WalFramesApplied);
        Assert.Equal(0, outcome.WalFramesRejected);
        Assert.Equal(2, outcome.PageCount);
        Assert.Equal(expected, File.ReadAllBytes(outcome.PlaintextPath));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ReaderRequiresFreshPageEvidenceForGrowthAndRegrowth(
        bool regrowth, bool completeGrowth, bool checksumBigEndian)
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"), rows: 40);
        var fixture = CreatePageEvidenceScenario(plaintext, regrowth, completeGrowth, checksumBigEndian);
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, fixture.Main);
        File.WriteAllBytes(encryptedPath + "-wal", fixture.Wal);
        using var cache = new SqlCipherDatabaseCache(new WeChatKeySet([fixture.DatabaseKey]));

        var outcome = cache.GetPlaintext(encryptedPath);

        Assert.Equal(fixture.ExpectedApplied, outcome.WalFramesApplied);
        Assert.Equal(completeGrowth ? 0 : 1, outcome.WalFramesRejected);
        Assert.Equal(completeGrowth ? 4 : 2, outcome.PageCount);
        Assert.Equal(fixture.ExpectedImage, File.ReadAllBytes(outcome.PlaintextPath));
        Assert.Equal(fixture.Main, File.ReadAllBytes(encryptedPath));
        Assert.Equal(fixture.Wal, File.ReadAllBytes(encryptedPath + "-wal"));
    }

    /// <summary>
    /// Authenticated growth fixtures shared with actual materializer-to-capture tests. Missing
    /// growth repeats page 4, so duplicate frames cannot stand in for absent page 3. Regrowth
    /// additionally puts an older page 3 in both the main image and WAL before truncating it.
    /// </summary>
    internal static PageEvidenceScenario CreatePageEvidenceScenario(
        byte[] plaintext, bool regrowth, bool completeGrowth, bool checksumBigEndian = false)
    {
        const int pageSize = SqlCipherPageCipher.PageSize;
        var mainPlain = plaintext[..((regrowth ? 4 : 2) * pageSize)];
        var main = EncryptDatabase(mainPlain, Key, Salt);
        var expected = mainPlain.ToArray();
        for (var page = 0; page < expected.Length / pageSize; page++)
        {
            expected.AsSpan(page * pageSize + SqlCipherPageCipher.IvOffset, SqlCipherPageCipher.ReserveSize).Clear();
        }

        var macKey = SqlCipherPageCipher.DeriveMacKey(Key, Salt);
        const uint salt1 = 0x32D1F04A;
        const uint salt2 = 0x3824DAB3;
        var wal = CreateWal(salt1, salt2, checksumBigEndian);
        void AddPage(int pageNumber, byte marker, uint commitSize)
        {
            wal.AddRange(BuildFrame(
                EncryptPage(Key, macKey, MarkedPage(plaintext, pageNumber - 1, marker), (uint)pageNumber, Salt),
                (uint)pageNumber, commitSize, salt1, salt2));
        }

        if (regrowth)
        {
            AddPage(3, 0x50, 4); // authenticated old WAL evidence must also be discarded
        }

        AddPage(2, 0x51, 2); // a prior valid commit must survive an invalid later growth
        expected = expected[..(2 * pageSize)];
        expected.AsSpan(pageSize, 64).Fill(0x51);

        AddPage(4, 0x52, 0); // out-of-order and repeated new page
        if (completeGrowth)
        {
            AddPage(3, 0x53, 0);
        }

        AddPage(4, 0x54, 4);
        SealWalChecksums(wal);
        if (completeGrowth)
        {
            var result = new byte[4 * pageSize];
            expected.CopyTo(result, 0);
            for (var page = 3; page <= 4; page++)
            {
                var fresh = MarkedPage(plaintext, page - 1, page == 3 ? (byte)0x53 : (byte)0x54);
                fresh.AsSpan(SqlCipherPageCipher.IvOffset).Clear();
                fresh.CopyTo(result, (page - 1) * pageSize);
            }

            expected = result;
        }

        return new PageEvidenceScenario(
            main, [.. wal], expected, (regrowth ? 2 : 1) + (completeGrowth ? 3 : 0),
            new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key));
    }

    internal sealed record PageEvidenceScenario(
        byte[] Main, byte[] Wal, byte[] ExpectedImage, int ExpectedApplied, WeChatDatabaseKey DatabaseKey);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlaintextWalScannerRejectsGrowthGapsBeforeSqliteCanRecover(bool regrowth)
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("seed.db"), rows: 40);
        var fixture = CreatePageEvidenceScenario(plaintext, regrowth, completeGrowth: false);
        var path = temp.Combine("plain.db");
        File.WriteAllBytes(path, plaintext[..((regrowth ? 4 : 2) * SqlCipherPageCipher.PageSize)]);
        // The plaintext scanner checks framing and page evidence without authentication. These
        // checksum-valid frame payloads are opaque to that scan; the missing page must reject the
        // WAL before SQLite backup is reached, regardless of its ability to recover a prefix.
        File.WriteAllBytes(path + "-wal", fixture.Wal);
        using var cache = new SqlCipherDatabaseCache(new WeChatKeySet([]));

        var exception = Assert.Throws<WeChatKeyUnavailableException>(() => cache.GetPlaintext(path));

        Assert.Contains("invalid or incomplete plaintext SQLite WAL evidence", exception.Message);
        Assert.Equal(fixture.Wal, File.ReadAllBytes(path + "-wal"));
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
        var sourceBytes = File.ReadAllBytes(path);

        var keys = new WeChatKeySet([]);
        using var cache = new SqlCipherDatabaseCache(keys);

        var outcome = cache.GetPlaintext(path);
        Assert.True(outcome.WasPlaintext);
        Assert.NotEqual(path, outcome.PlaintextPath);
        Assert.Equal(5L, SqliteConnectionPooledRowCount(outcome.PlaintextPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(path));
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
        Assert.NotEqual(artifact, first.PlaintextPath);

        // A changed source gets a new scratch snapshot. The input database remains caller-owned
        // and must not be deleted when its cache fingerprint changes.
        File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddMinutes(1));

        var second = cache.GetPlaintext(artifact);

        Assert.True(File.Exists(artifact));
        Assert.NotEqual(artifact, second.PlaintextPath);
        Assert.NotEqual(first.PlaintextPath, second.PlaintextPath);
        Assert.True(second.WasPlaintext);
        Assert.Equal(5L, SqliteConnectionPooledRowCount(first.PlaintextPath));
        Assert.Equal(5L, SqliteConnectionPooledRowCount(second.PlaintextPath));
        Assert.Equal(5L, SqliteConnectionPooledRowCount(artifact));
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

    [Fact]
    public void ReaderRejectsAnUnauthenticatedMainDatabasePage()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"));
        var encryptedPath = temp.Combine("message_0.db");
        var encrypted = EncryptDatabase(plaintext, Key, Salt);
        encrypted[SqlCipherPageCipher.PageSize + 100] ^= 0x80;
        File.WriteAllBytes(encryptedPath, encrypted);
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);

        Assert.Throws<WeChatKeyUnavailableException>(() => cache.GetPlaintext(encryptedPath));
    }

    [Fact]
    public void ReaderRejectsAnIncompleteEncryptedMainPageTail()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"));
        var encryptedPath = temp.Combine("message_0.db");
        var encrypted = EncryptDatabase(plaintext, Key, Salt);
        File.WriteAllBytes(encryptedPath, [.. encrypted, 0x42]);
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);

        Assert.Throws<WeChatKeyUnavailableException>(() => cache.GetPlaintext(encryptedPath));
    }

    [Fact]
    public void ReaderMaterializesCommittedPlaintextWalRowsIntoTheSnapshot()
    {
        using var temp = new TempDirectory();
        var source = CreateWriteAheadLogOnlyDatabasePair(temp.Path);
        var sourceMain = File.ReadAllBytes(source);
        var sourceWal = File.ReadAllBytes(source + "-wal");
        using var cache = new SqlCipherDatabaseCache(new WeChatKeySet([]));

        var outcome = cache.GetPlaintext(source);

        Assert.True(outcome.WasPlaintext);
        Assert.True(outcome.PageCount > 0);
        Assert.Equal(5L, SqliteConnectionPooledRowCount(outcome.PlaintextPath));
        Assert.Equal(sourceMain, File.ReadAllBytes(source));
        Assert.Equal(sourceWal, File.ReadAllBytes(source + "-wal"));
    }

    [Fact]
    public void ReaderRejectsAnIncompletePlaintextDatabaseTail()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("plain.db");
        var database = CreatePlaintextDatabase(path);
        File.WriteAllBytes(path, [.. database, 0x42]);
        using var cache = new SqlCipherDatabaseCache(new WeChatKeySet([]));

        Assert.Throws<WeChatKeyUnavailableException>(() => cache.GetPlaintext(path));
    }

    [Fact]
    public void ReaderReportsAnInvalidWalHeaderInsteadOfSilentlyIgnoringIt()
    {
        using var temp = new TempDirectory();
        var plaintext = CreatePlaintextDatabase(temp.Combine("plain.db"));
        var encryptedPath = temp.Combine("message_0.db");
        File.WriteAllBytes(encryptedPath, EncryptDatabase(plaintext, Key, Salt));
        File.WriteAllBytes(encryptedPath + "-wal", [1, 2, 3]);
        var keys = new WeChatKeySet([new WeChatDatabaseKey(Convert.ToHexString(Salt).ToLowerInvariant(), Key)]);
        using var cache = new SqlCipherDatabaseCache(keys);

        Assert.Equal(1, cache.GetPlaintext(encryptedPath).WalFramesRejected);
    }

    [Fact]
    public void GenericIntegrityCheckDoesNotClaimExternalContentFtsIndexConsistency()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("fts.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TABLE documents(id INTEGER PRIMARY KEY, body TEXT); " +
                "INSERT INTO documents VALUES (1, 'preserved source text'); " +
                "CREATE VIRTUAL TABLE documents_fts USING fts5(body, content='documents', content_rowid='id');";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", command.ExecuteScalar());
            command.CommandText = "INSERT INTO documents_fts(documents_fts, rank) VALUES ('integrity-check', 1);";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
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

    private static List<byte> CreateWal(uint salt1, uint salt2, bool checksumBigEndian = false)
    {
        var wal = new List<byte>();
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checksumBigEndian ? 0x377F0683u : 0x377F0682u);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 3_007_000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), SqlCipherPageCipher.PageSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), salt2);
        wal.AddRange(header);
        return wal;
    }

    private static void SealWalChecksums(List<byte> wal)
    {
        var checksumBigEndian = BinaryPrimitives.ReadUInt32BigEndian(CollectionsMarshal.AsSpan(wal)[..4]) == 0x377F0683;
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(CollectionsMarshal.AsSpan(wal).Slice(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(CollectionsMarshal.AsSpan(wal).Slice(20, 4));
        uint s0 = 0;
        uint s1 = 0;
        UpdateWalChecksum(CollectionsMarshal.AsSpan(wal)[..24], checksumBigEndian, ref s0, ref s1);
        BinaryPrimitives.WriteUInt32BigEndian(CollectionsMarshal.AsSpan(wal).Slice(24, 4), s0);
        BinaryPrimitives.WriteUInt32BigEndian(CollectionsMarshal.AsSpan(wal).Slice(28, 4), s1);

        const int frameSize = 24 + SqlCipherPageCipher.PageSize;
        for (var offset = 32; offset + frameSize <= wal.Count; offset += frameSize)
        {
            var frame = CollectionsMarshal.AsSpan(wal).Slice(offset, frameSize);
            // The fixture deliberately includes one frame from an old salt generation after
            // the valid frame; SQLite stops at that boundary, so its checksum is immaterial.
            if (BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(8, 4)) != salt1 ||
                BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(12, 4)) != salt2)
            {
                break;
            }

            UpdateWalChecksum(frame[..8], checksumBigEndian, ref s0, ref s1);
            UpdateWalChecksum(frame.Slice(24, SqlCipherPageCipher.PageSize), checksumBigEndian, ref s0, ref s1);
            BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(16, 4), s0);
            BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(20, 4), s1);
        }
    }

    private static void UpdateWalChecksum(ReadOnlySpan<byte> bytes, bool bigEndian, ref uint s0, ref uint s1)
    {
        for (var i = 0; i < bytes.Length; i += 8)
        {
            var word0 = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i, 4));
            var word1 = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + 4, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 4, 4));
            s0 = unchecked(s0 + word0 + s1);
            s1 = unchecked(s1 + word1 + s0);
        }
    }
}
