using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeArchive.Core.Domain;
using WeArchive.Core.Normalization;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.WeChat.Compatibility;
using WeArchive.Infrastructure.WeChat.Parsers;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// M2a semantic quality baseline (Issue #97, Roadmap M2a).
/// <para>
/// The corpus (Support/SemanticBaselineCorpus.cs) is deterministic and synthetic. These
/// tests measure the shipped parser → normalizer boundary over the corpus and pin the
/// measured baseline. The pinned numbers are mirrored in
/// docs/SEMANTIC_QUALITY_BASELINE.md; when an M2b/M2c/M2d change intentionally improves
/// classification, update the pinned values here AND the report tables in the same PR,
/// recording the before/after in the report. The measurement must never weaken the
/// invariants below: no record is dropped, unknown stays explicit, unavailable fields
/// stay null, and the offline import/export run is byte-deterministic.
/// </para>
/// </summary>
public sealed class SemanticBaselineTests
{
    private static readonly int TotalRecords = SemanticBaselineCorpus.Records.Count;

    private static IReadOnlyList<(string ConversationId, ConversationKind Kind)> Conversations =>
        SemanticBaselineCorpus.Records
            .Select(r => r.SourceConversationId)
            .Distinct()
            .Select(id => (id, WeChat4Schema.ClassifyConversation(id)))
            .OrderBy(c => c.id, StringComparer.Ordinal)
            .ToList();

    // ---- measurement harness ----------------------------------------------------------

    private sealed record MeasuredRecord(SourceMessage Source, CanonicalMessage Message);

    private static NormalizationContext Context(string sourceConversationId) => new()
    {
        AccountId = SemanticBaselineCorpus.SourceProfileId,
        ConversationId = StableIds.Conversation(
            StableIds.Account(SemanticBaselineCorpus.AdapterName, SemanticBaselineCorpus.SourceProfileId),
            WeChat4Schema.ClassifyConversation(sourceConversationId),
            sourceConversationId,
            string.Equals(sourceConversationId, SemanticBaselineCorpus.DirectConversation, StringComparison.Ordinal)
                ? SemanticBaselineCorpus.DirectConversation
                : null),
        SourceProfileId = SemanticBaselineCorpus.SourceProfileId,
        AdapterName = SemanticBaselineCorpus.AdapterName,
        AdapterVersion = SemanticBaselineCorpus.AdapterVersion,
        SourceVersion = SemanticBaselineCorpus.CorpusSourceVersion,
        ImportRunId = "baseline_measurement_1",
        ResolveParticipantId = sourceId => StableIds.Participant(SemanticBaselineCorpus.SourceProfileId, sourceId),
    };

    /// <summary>
    /// Runs the whole corpus through the shipped parser and normalizer once. Pure, offline,
    /// deterministic: no live client, no key, no filesystem, no network.
    /// </summary>
    private static IReadOnlyList<MeasuredRecord> MeasureOnce() =>
        [.. Conversations.SelectMany(c => SemanticBaselineSourceAdapter
            .BuildMessages(c.ConversationId)
            .Select(m => new MeasuredRecord(m, MessageNormalizer.Normalize(m, Context(c.ConversationId)))))];

    private static IReadOnlyList<MeasuredRecord> Measure() => MeasureOnce();

    /// <summary>
    /// The ingest diagnostic contract of ImportService (Core/Services/ImportService.cs):
    /// unknown → unknown_message_type; other partial → partial_app_message; a reply with no
    /// locally available quote snapshot → unresolved_reply_target.
    /// </summary>
    private static string? IngestDiagnosticCode(CanonicalMessage message) =>
        message.Type == CanonicalMessageType.Unknown ? DiagnosticCodes.UnknownMessageType
        : message.IsPartial ? DiagnosticCodes.PartialAppMessage
        : message.ReplyTo is { Text: null } ? DiagnosticCodes.UnresolvedReplyTarget
        : null;

    private static string EnvelopeProjection(CanonicalMessage m) =>
        string.Join("|",
            m.Id,
            m.ConversationId,
            m.SenderId,
            m.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            m.Type.ToWireName(),
            m.Text,
            m.Payload?.ToJsonString() ?? "null",
            m.ReplyTo?.MessageId,
            m.ReplyTo?.SourceMessageId,
            m.ReplyTo?.SenderId,
            m.ReplyTo?.SenderName,
            m.ReplyTo?.Text,
            m.ReplyTo?.OccurredAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            m.Source.SourceMessageId,
            m.Source.SourceType,
            m.Source.SourceSubtype,
            m.Source.SourcePartition,
            m.Source.SourceOrderKey,
            m.Source.SourceVersion,
            m.Source.AdapterVersion,
            m.IsPartial);

    private static MeasuredRecord Single(IReadOnlyList<MeasuredRecord> measured, string recordId)
    {
        var record = SemanticBaselineCorpus.Records.Single(r => string.Equals(r.RecordId, recordId, StringComparison.Ordinal));
        return measured.Single(m => string.Equals(m.Source.SourceMessageId, SemanticBaselineSourceAdapter.SourceMessageIdFor(record), StringComparison.Ordinal));
    }

    /// <summary>Corpus records keyed by the upstream identity the test adapter archives them under.</summary>
    private static IReadOnlyDictionary<string, SemanticBaselineRecord> RecordsBySourceMessageId { get; } =
        SemanticBaselineCorpus.Records.ToDictionary(
            r => SemanticBaselineSourceAdapter.SourceMessageIdFor(r),
            r => r,
            StringComparer.Ordinal);

    // ---- pinned classification baseline ------------------------------------------------

    [Fact]
    public void CorpusClassificationMatchesThePinnedBaselineReport()
    {
        var measured = Measure();

        // 33 M2a records + 4 additive M2b records (transfer_without_amount,
        // redpacket_without_amount, groupcollect_unsampled_shape, paycard_titleless_unknown;
        // Issue #98) + 7 additive M2c records (the reply reference matrix and the revoke
        // null-fallback; Issue #99).
        Assert.Equal(44, TotalRecords);
        Assert.Equal(TotalRecords, measured.Count);

        var byType = measured
            .GroupBy(m => m.Message.Type.ToWireName(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["text"] = 12,
            ["image"] = 1,
            ["voice"] = 3,
            ["video"] = 1,
            ["emoji"] = 1,
            ["file"] = 2,
            ["link"] = 3,
            ["app_share"] = 2,
            ["mini_program"] = 1,
            ["forward_bundle"] = 2,
            ["location"] = 1,
            ["contact_card"] = 1,
            ["system"] = 3,
            ["revoke"] = 2,
            ["red_packet"] = 2,
            ["transfer"] = 2,
            ["unknown"] = 5,
        };

        Assert.Equal(TotalRecords, expected.Values.Sum());
        foreach (var (wire, count) in expected)
        {
            var message = $"canonical type '{wire}': expected {count}, measured {byType.GetValueOrDefault(wire)}. " +
                "If this change is intentional (M2b/M2c/M2d), update the pinned values here and the " +
                "tables in docs/SEMANTIC_QUALITY_BASELINE.md in the same PR, with before/after evidence.";
            if (count == 0)
            {
                Assert.True(!byType.ContainsKey(wire), message);
            }
            else
            {
                Assert.True(byType.TryGetValue(wire, out var actual) && actual == count, message);
            }
        }

        // Compare only types the corpus expects to occur; zero-count keys stay absent.
        Assert.Equal(
            expected.Where(kv => kv.Value > 0).Select(kv => kv.Key).Order(StringComparer.Ordinal),
            byType.Keys.Order(StringComparer.Ordinal));

        var byConversation = measured
            .GroupBy(m => m.Source.SourceConversationId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Assert.Equal(31, byConversation[SemanticBaselineCorpus.DirectConversation]);
        Assert.Equal(8, byConversation[SemanticBaselineCorpus.GroupConversation]);
        Assert.Equal(2, byConversation[SemanticBaselineCorpus.OfficialConversation]);
        Assert.Equal(3, byConversation[SemanticBaselineCorpus.SystemConversation]);

        Assert.Equal(ConversationKind.Direct, WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.DirectConversation));
        Assert.Equal(ConversationKind.Group, WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.GroupConversation));
        Assert.Equal(ConversationKind.Official, WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.OfficialConversation));
        Assert.Equal(ConversationKind.System, WeChat4Schema.ClassifyConversation(SemanticBaselineCorpus.SystemConversation));

        var diagnostics = measured
            .Select(m => IngestDiagnosticCode(m.Message))
            .Where(code => code is not null)
            .GroupBy(code => code!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Assert.Equal(5, diagnostics.GetValueOrDefault(DiagnosticCodes.UnknownMessageType));
        Assert.Equal(7, diagnostics.GetValueOrDefault(DiagnosticCodes.PartialAppMessage));
        Assert.Equal(0, diagnostics.GetValueOrDefault(DiagnosticCodes.UnresolvedReplyTarget));

        var partial = measured.Count(m => m.Message.IsPartial);
        Assert.Equal(12, partial);
    }

    [Fact]
    public void EveryCorpusRecordReachesTheCanonicalBoundaryWithItsCodesPreserved()
    {
        var measured = Measure();

        Assert.Equal(TotalRecords, measured.Count);

        var recordsById = RecordsBySourceMessageId;
        Assert.Equal(TotalRecords, recordsById.Count);

        foreach (var m in measured)
        {
            var record = recordsById[m.Source.SourceMessageId!];
            Assert.Equal(record.Type.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Source.SourceType);
            Assert.Equal(record.SubType.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Source.SourceSubtype);
            Assert.Equal(SemanticBaselineCorpus.CorpusSourceVersion, m.Message.Source.SourceVersion);
            Assert.Equal(record.SourceConversationId, m.Source.SourceConversationId);
        }

        // Unknown remains explicit: every unknown keeps its verbatim upstream codes in the
        // payload, stays partial and is never silently dropped. MESSAGE_SCHEMA section 18.
        foreach (var m in measured.Where(x => x.Message.Type == CanonicalMessageType.Unknown))
        {
            var record = recordsById[m.Source.SourceMessageId!];
            Assert.True(m.Message.IsPartial);
            Assert.Equal(record.Type.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Message.Payload!["source_type"]!.GetValue<string>());
            Assert.Equal(record.SubType.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Message.Payload!["source_subtype"]!.GetValue<string>());
            Assert.True(m.Message.Payload!["raw_summary"] is null);
            Assert.Equal(DiagnosticCodes.UnknownMessageType, IngestDiagnosticCode(m.Message));
        }
    }

    // ---- nested text preservation ------------------------------------------------------

    [Fact]
    public void QuoteSnapshotsArePreservedWithoutFabricatingATargetId()
    {
        var measured = Measure();
        var quote = Single(measured, "quote_direct");

        Assert.Equal(CanonicalMessageType.Text, quote.Message.Type);
        Assert.Equal("我同意这个方案", quote.Message.Text);
        Assert.NotNull(quote.Message.ReplyTo);
        var reply = quote.Message.ReplyTo!;
        Assert.Null(reply.MessageId); // resolved later by the archive; never guessed at normalization
        // Baseline defect E1 (Issue #99): the adapter composes the raw upstream svrid into the
        // documented `s:<server_id>` identity space (docs/DATA_MODEL.md section 16.2) instead of
        // storing an unwrapped svrid that could never match an archived target. The snapshot
        // fields below are unchanged from the M2a baseline.
        Assert.Equal("s:954009715970562000", reply.SourceMessageId);
        Assert.Equal(StableIds.Participant(SemanticBaselineCorpus.SourceProfileId, "wxid_bob"), reply.SenderId);
        Assert.Equal("张三", reply.SenderName);
        Assert.Equal("下午三点开会。", reply.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1776678109), reply.OccurredAt);
        Assert.Null(IngestDiagnosticCode(quote.Message)); // snapshot present → no unresolved_reply_target
    }

    // ---- M2c reply/quote/forward reference matrix (Issue #99) ---------------------------

    [Fact]
    public void ReplyReferenceMatrixResolvesOnlyAgainstArchivedServerIdIdentity()
    {
        var measured = Measure();

        // Resolved candidate at the normalization boundary: the composed reference is present,
        // the canonical target id is still null because resolution happens in the archive.
        var resolved = Single(measured, "m2c_quote_resolved");
        Assert.Equal("s:954009715971000100", resolved.Message.ReplyTo!.SourceMessageId);
        Assert.Null(resolved.Message.ReplyTo.MessageId);
        Assert.Equal("上午十点评审。", resolved.Message.ReplyTo.Text);

        // The reference target is archived under the documented composite server id.
        var target = Single(measured, "m2c_quote_target_text");
        Assert.Equal("s:954009715971000100", target.Source.SourceMessageId);
        Assert.Null(target.Message.ReplyTo);

        // Absent target: the svrid names no archived record; the snapshot survives untouched.
        var absent = Single(measured, "m2c_quote_target_absent");
        Assert.Equal("s:954009715971000200", absent.Message.ReplyTo!.SourceMessageId);
        Assert.Null(absent.Message.ReplyTo.MessageId);
        Assert.Equal("一条没有落库的引用", absent.Message.ReplyTo.Text);
        Assert.Equal("李四", absent.Message.ReplyTo.SenderName);

        // Unaddressable target: a record with the exact quoted text exists, but without a
        // server id it is only addressable by its partition-local composite — the resolver
        // must not fall back to content matching and must leave the target null.
        var unaddressableTarget = Single(measured, "m2c_quote_target_unaddressable");
        Assert.Equal("sb:m2c_quote_target_unaddressable", unaddressableTarget.Source.SourceMessageId);
        var unaddressable = Single(measured, "m2c_quote_unaddressable_target");
        Assert.Equal("s:954009715971000300", unaddressable.Message.ReplyTo!.SourceMessageId);
        Assert.Null(unaddressable.Message.ReplyTo.MessageId);
        Assert.Equal("这版PPT我晚上发你。", unaddressable.Message.ReplyTo.Text);

        // Malformed reference: no svrid at all; nothing addressable remains and nothing is
        // invented, while the locally available snapshot is preserved verbatim.
        var malformed = Single(measured, "m2c_quote_without_svrid");
        Assert.Null(malformed.Message.ReplyTo!.SourceMessageId);
        Assert.Null(malformed.Message.ReplyTo.MessageId);
        Assert.Equal("没有服务端引用号的引用", malformed.Message.ReplyTo.Text);
        Assert.Equal("张三", malformed.Message.ReplyTo.SenderName);
        Assert.All(
            new[] { absent, unaddressable, malformed },
            m => Assert.Null(IngestDiagnosticCode(m.Message))); // snapshots present → no unresolved_reply_target
    }

    [Fact]
    public void RevokeTargetFollowsTheDocumentedNullFallback()
    {
        var measured = Measure();

        // With a locally recorded newmsgid the payload carries the deterministic derivation
        // over the upstream id; without one the field stays null. The linkage between a
        // revokemsg newmsgid and an archived message identity is not established by the
        // sampled evidence, so nothing is resolved or guessed here (baseline E2, Issue #99).
        var withNewMsgId = Single(measured, "revoke_group");
        Assert.Equal(CanonicalMessageType.Revoke, withNewMsgId.Message.Type);
        Assert.Equal(
            StableIds.Message(withNewMsgId.Message.ConversationId, "954009715970562000"),
            withNewMsgId.Message.Payload!["revoked_message_id"]!.GetValue<string>());
        // The revoke sentence is the semantic text; payload revoked_text stays null by the
        // shipped contract (MESSAGE_SCHEMA section 14: retain content only when the archived
        // state legitimately carries it — the wire record never does).
        Assert.True(withNewMsgId.Message.Payload!["revoked_text"] is null);
        Assert.Equal("[撤回消息] 你撤回了一条消息", withNewMsgId.Message.Text);

        var withoutNewMsgId = Single(measured, "m2c_revoke_without_newmsgid");
        Assert.Equal(CanonicalMessageType.Revoke, withoutNewMsgId.Message.Type);
        Assert.True(withoutNewMsgId.Message.Payload!["revoked_message_id"] is null);
        Assert.True(withoutNewMsgId.Message.Payload!["revoked_text"] is null);
        Assert.Equal("[撤回消息] 王五撤回了一条消息", withoutNewMsgId.Message.Text);
        Assert.False(withoutNewMsgId.Message.IsPartial);
    }

    [Fact]
    public void ForwardBundlesPreserveNestedTextIncludingUnknownItems()
    {
        var measured = Measure();
        var structured = Single(measured, "bundle_group_structured");

        Assert.Equal(CanonicalMessageType.ForwardBundle, structured.Message.Type);
        Assert.False(structured.Message.IsPartial);
        Assert.Equal(3, structured.Message.Payload!["item_count"]!.GetValue<int>());
        var items = Assert.IsType<JsonArray>(structured.Message.Payload!["items"]);
        Assert.Equal(3, items.Count);

        var first = items[0]!.AsObject();
        Assert.Equal("text", first["type"]!.GetValue<string>());
        Assert.Equal("第一版价格是不是太高了？", first["text"]!.GetValue<string>());
        Assert.Equal("张三", first["sender_name"]!.GetValue<string>());
        Assert.True(first["sender_id"] is null); // nested identity is not resolvable; never invented

        Assert.Equal("image", items[1]!.AsObject()["type"]!.GetValue<string>());

        // A nested item the parser does not model stays 'unknown' inside the preserved bundle.
        var third = items[2]!.AsObject();
        Assert.Equal("unknown", third["type"]!.GetValue<string>());
        Assert.Equal("不支持类型的嵌套条目", third["text"]!.GetValue<string>());

        // WeChat 4.x does not persist per-item timestamps; they must stay null. MESSAGE_SCHEMA section 12.
        Assert.All(items, item => Assert.True(item!.AsObject()["time"] is null));

        var fallback = Single(measured, "bundle_group_description_fallback");
        Assert.Equal(CanonicalMessageType.ForwardBundle, fallback.Message.Type);
        Assert.True(fallback.Message.IsPartial); // structured list unavailable; summary fallback is explicitly partial
        var fallbackItems = Assert.IsType<JsonArray>(fallback.Message.Payload!["items"]);
        Assert.Equal(2, fallbackItems.Count);
        Assert.Equal("张三", fallbackItems[0]!.AsObject()["sender_name"]!.GetValue<string>());
        Assert.Equal("第一版价格是不是太高了", fallbackItems[0]!.AsObject()["text"]!.GetValue<string>());
        Assert.Equal("李四", fallbackItems[1]!.AsObject()["sender_name"]!.GetValue<string>());
        Assert.Equal("收到", fallbackItems[1]!.AsObject()["text"]!.GetValue<string>());
    }

    [Fact]
    public void GroupPrefixAndSystemTemplatesPreserveTheHumanReadableText()
    {
        var measured = Measure();

        var known = Single(measured, "text_group_known_sender");
        Assert.Equal(CanonicalMessageType.Text, known.Message.Type);
        Assert.Equal("下午三点开会。", known.Message.Text);
        Assert.Equal(StableIds.Participant(SemanticBaselineCorpus.SourceProfileId, "wxid_alice"), known.Message.SenderId);

        // The prefix only strips when it names a known participant; the text stays verbatim.
        // The sender still resolves from the shard's real sender id (the prefix hint is null),
        // which is the M2d-relevant behavior this record pins.
        var unmatched = Single(measured, "text_group_unmatched_prefix");
        Assert.Equal("注意:\n下午三点开会。", unmatched.Message.Text);
        Assert.Equal(StableIds.Participant(SemanticBaselineCorpus.SourceProfileId, "wxid_dave"), unmatched.Message.SenderId);

        var template = Single(measured, "system_group_template");
        Assert.Equal(CanonicalMessageType.System, template.Message.Type);
        Assert.Equal("\"张三\"邀请\"李四\"加入了群聊", template.Message.Text);
        Assert.Null(template.Message.SenderId);

        var plain = Single(measured, "system_plain_event");
        Assert.Equal(CanonicalMessageType.System, plain.Message.Type);
        Assert.Equal("戳了戳你", plain.Message.Text);
    }

    // ---- unavailability stays explicit (no fabricated values) ---------------------------

    [Fact]
    public void UnavailableFieldsStayNullAndAreFlaggedPartial()
    {
        var measured = Measure();

        var untimed = Single(measured, "voice_direct_untimed");
        Assert.Equal(CanonicalMessageType.Voice, untimed.Message.Type);
        Assert.True(untimed.Message.Payload!["duration_seconds"] is null);
        Assert.True(untimed.Message.IsPartial);
        Assert.Equal("[语音]", untimed.Message.Text);

        var timed = Single(measured, "voice_direct_timed");
        Assert.Equal(6, timed.Message.Payload!["duration_seconds"]!.GetValue<int>());
        Assert.False(timed.Message.IsPartial);

        var wrapper = Single(measured, "link_direct_wrapper_only");
        Assert.Equal(CanonicalMessageType.Link, wrapper.Message.Type);
        Assert.True(wrapper.Message.Payload!["original_url"] is null);
        Assert.Equal("https://support.weixin.qq.com/redirect?x=1", wrapper.Message.Payload!["fallback_url"]!.GetValue<string>());

        var unnamed = Single(measured, "file_direct_unnamed");
        Assert.Equal(CanonicalMessageType.File, unnamed.Message.Type);
        Assert.True(unnamed.Message.Payload!["filename"] is null);
        Assert.True(unnamed.Message.IsPartial);
        Assert.Equal("[文件]", unnamed.Message.Text);

        var named = Single(measured, "file_direct_named");
        Assert.Equal("项目季度汇报模板.docx", named.Message.Payload!["filename"]!.GetValue<string>());
        Assert.False(named.Message.IsPartial);

        // Baseline defect D1 (Issue #98): the transfer subtype is classified from its local
        // pay-card semantics, and the readable fee description becomes the amount. Nothing
        // is guessed: currency/status stay null until a sampled field carries them.
        var transfer = Single(measured, "transfer_unclassified");
        Assert.Equal(CanonicalMessageType.Transfer, transfer.Message.Type);
        Assert.False(transfer.Message.IsPartial);
        Assert.Equal("[转账] 200.00 元", transfer.Message.Text);
        Assert.Equal("200.00", transfer.Message.Payload!["amount"]!.GetValue<string>());
        Assert.True(transfer.Message.Payload!["currency"] is null);
        Assert.True(transfer.Message.Payload!["status"] is null);
        Assert.Null(IngestDiagnosticCode(transfer.Message));

        // A pay card without a readable fee description stays an explicitly partial transfer.
        var transferWithoutAmount = Single(measured, "transfer_without_amount");
        Assert.Equal(CanonicalMessageType.Transfer, transferWithoutAmount.Message.Type);
        Assert.True(transferWithoutAmount.Message.IsPartial);
        Assert.Equal("[转账]", transferWithoutAmount.Message.Text);
        Assert.True(transferWithoutAmount.Message.Payload!["amount"] is null);
        Assert.Equal(DiagnosticCodes.PartialAppMessage, IngestDiagnosticCode(transferWithoutAmount.Message));

        // Baseline defect D2 (Issue #98): red packets expose the readable amount; the
        // shipped null-payload shape is preserved when nothing is readable.
        var redPacket = Single(measured, "redpacket_direct");
        Assert.Equal(CanonicalMessageType.RedPacket, redPacket.Message.Type);
        Assert.False(redPacket.Message.IsPartial);
        Assert.Equal("66.00", redPacket.Message.Payload!["amount"]!.GetValue<string>());
        Assert.True(redPacket.Message.Payload!["currency"] is null);
        Assert.True(redPacket.Message.Payload!["status"] is null);

        var redPacketWithoutAmount = Single(measured, "redpacket_without_amount");
        Assert.Equal(CanonicalMessageType.RedPacket, redPacketWithoutAmount.Message.Type);
        Assert.Null(redPacketWithoutAmount.Message.Payload);
        Assert.False(redPacketWithoutAmount.Message.IsPartial);

        // The transfer classification is bounded to the sampled 49/2001 shape (review round
        // 1, P1-1): every other wcpayinfo shape keeps the documented app_share partial
        // fallback, its locally present fee description is not copied into any payload, and
        // the card type is never guessed from the pay payload alone.
        var groupCollect = Single(measured, "groupcollect_unsampled_shape");
        Assert.Equal(CanonicalMessageType.AppShare, groupCollect.Message.Type);
        Assert.True(groupCollect.Message.IsPartial);
        Assert.Equal("群收款卡片占位", groupCollect.Message.Payload!["title"]!.GetValue<string>());
        Assert.True(groupCollect.Message.Payload!["amount"] is null);

        // A wcpayinfo card without any title/description/url stays the explicit unknown
        // fallback (U4) instead of being guessed into transfer.
        var titleless = Single(measured, "paycard_titleless_unknown");
        Assert.Equal(CanonicalMessageType.Unknown, titleless.Message.Type);
        Assert.True(titleless.Message.IsPartial);
        Assert.Equal("49", titleless.Message.Payload!["source_type"]!.GetValue<string>());
        Assert.Equal("2005", titleless.Message.Payload!["source_subtype"]!.GetValue<string>());
        Assert.Equal(DiagnosticCodes.UnknownMessageType, IngestDiagnosticCode(titleless.Message));

        var original = Single(measured, "link_direct_original");
        Assert.Equal("https://example.com/article/123", original.Message.Payload!["original_url"]!.GetValue<string>());
        Assert.True(original.Message.Payload!["fallback_url"] is null);
    }

    // ---- determinism --------------------------------------------------------------------

    [Fact]
    public void NormalizationOfTheCorpusIsDeterministic()
    {
        var first = MeasureOnce().Select(m => EnvelopeProjection(m.Message)).ToList();
        var second = MeasureOnce().Select(m => EnvelopeProjection(m.Message)).ToList();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task OfflineImportArchiveExportIsByteDeterministicAndCountsUnknowns()
    {
        using var temp = new TempDirectory();
        var first = await ImportAndExportAsync(temp.Combine("run1"));
        var second = await ImportAndExportAsync(temp.Combine("run2"));

        // A fresh canonical archive rebuilt offline from the same corpus exports the exact
        // same dataset: deterministic rebuild/export at the canonical layer. Raw-Vault-only
        // rebuild determinism remains covered by RebuildServiceTests.
        Assert.Equal(44, first.Lines.Count);
        Assert.Equal(first.Lines, second.Lines);

        Assert.Equal(5, first.Counters.Unknown);
        Assert.Equal(12, first.Counters.Partial);
        Assert.Equal(5, first.Counters.Unsupported);
        Assert.Equal(44, first.Counters.Records);
        Assert.Equal(first.Counters, second.Counters);

        var unknownLines = first.Lines
            .Select(line => JsonDocument.Parse(line))
            .Where(d => d.RootElement.GetProperty("type").GetString() == "unknown")
            .ToList();
        Assert.Equal(5, unknownLines.Count);
        Assert.All(unknownLines, d =>
        {
            // The exported envelope has no per-line partial flag (archive-level manifest
            // counters carry it); unknowns keep their upstream codes in the payload.
            Assert.True(d.RootElement.TryGetProperty("payload", out var payload));
            Assert.NotNull(payload.GetProperty("source_type").GetString());
            Assert.NotNull(payload.GetProperty("source_subtype").GetString());
        });
    }

    [Fact]
    public async Task IngestDiagnosticsRollUpWithoutFatalFindings()
    {
        using var temp = new TempDirectory();
        var diagnostics = await ImportCorpusAsync(temp.Combine("diagnostics"));

        var totals = diagnostics
            .GroupBy(d => d.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.Ordinal);

        Assert.Equal(5, totals.GetValueOrDefault(DiagnosticCodes.UnknownMessageType));
        Assert.Equal(7, totals.GetValueOrDefault(DiagnosticCodes.PartialAppMessage));
        Assert.Equal(0, totals.GetValueOrDefault(DiagnosticCodes.UnresolvedReplyTarget));
        // Baseline defect E1 (Issue #99): the direct conversation now resolves the one reply
        // whose quoted record is archived under a server id, which surfaces as the documented
        // Info diagnostic. No snapshot-only quote carries a resolvable target.
        Assert.Equal(1, totals.GetValueOrDefault(DiagnosticCodes.ReplySnapshotOnly));
        Assert.Equal(0, diagnostics.Count(d => d.Severity == DiagnosticSeverity.Fatal));
    }

    [Fact]
    public async Task ReplyTargetsResolveInTheFullPipelineWithoutCrossIdentityGuessing()
    {
        using var temp = new TempDirectory();
        var adapter = new SemanticBaselineSourceAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(Path.Combine(temp.Path, "archive.db"), clock);
        var importer = new ImportService(adapter, store, clock);

        var diagnostics = new List<ImportDiagnostic>();
        foreach (var conversation in Conversations)
        {
            var peer = conversation.Kind == ConversationKind.Direct ? conversation.ConversationId : null;
            var outcome = await importer.ImportConversationAsync(new ImportRequest
            {
                SourceProfileId = SemanticBaselineCorpus.SourceProfileId,
                SourceConversationId = conversation.ConversationId,
                Kind = conversation.Kind,
                PeerSourceUserId = peer,
                ConversationTitle = "语义基线语料占位",
            }, null, CancellationToken.None);
            diagnostics.AddRange(outcome.Diagnostics);
        }

        // Exactly one reply reference resolves against the archive: the one whose quoted
        // record is archived under the documented server-id composite (Issue #99).
        Assert.Equal(1, diagnostics.Count(d => d.Code == DiagnosticCodes.ReplySnapshotOnly));

        var accountStableId = StableIds.Account(adapter.AdapterName, SemanticBaselineCorpus.SourceProfileId);
        var directStableId = StableIds.Conversation(
            accountStableId,
            ConversationKind.Direct,
            SemanticBaselineCorpus.DirectConversation,
            SemanticBaselineCorpus.DirectConversation);
        var direct = (await store.ReadMessagesAsync(directStableId, CancellationToken.None)).ToList();

        var resolved = Assert.Single(direct, m => m.Source.SourceMessageId == "sb:m2c_quote_resolved");
        Assert.Equal(
            StableIds.Message(directStableId, "s:954009715971000100"),
            resolved.ReplyTo!.MessageId);
        Assert.Equal("上午十点评审。", resolved.ReplyTo.Text);

        // Every reference without an addressable archived target stays null while its
        // snapshot survives; no target id is fabricated and no content matching happens.
        foreach (var recordId in new[]
                 {
                     "quote_direct", "m2c_quote_target_absent", "m2c_quote_unaddressable_target",
                 })
        {
            var unresolved = Assert.Single(direct, m => m.Source.SourceMessageId == $"sb:{recordId}");
            Assert.Null(unresolved.ReplyTo!.MessageId);
            Assert.NotNull(unresolved.ReplyTo.Text);
        }

        var malformed = Assert.Single(direct, m => m.Source.SourceMessageId == "sb:m2c_quote_without_svrid");
        Assert.Null(malformed.ReplyTo!.MessageId);
        Assert.Null(malformed.ReplyTo.SourceMessageId);
        Assert.Equal("没有服务端引用号的引用", malformed.ReplyTo.Text);

        // The exported dataset carries the resolved relationship and keeps the unresolvable
        // ones explicit: round-trip through export is part of the reference matrix.
        var workflow = TestWorkflows.CreateExportWorkflow(new JsonlDatasetExporter(store), store, clock);
        var exportDirectory = Path.Combine(temp.Path, "export", SemanticBaselineCorpus.DirectConversation);
        var export = await workflow.ExportConversationAsync(
            new ExportConversationRequest { ConversationId = directStableId, OutputDirectory = exportDirectory },
            null,
            CancellationToken.None);
        Assert.True(export.Succeeded);

        var lines = Directory
            .EnumerateFiles(exportDirectory, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadAllLines(path, System.Text.Encoding.UTF8).Where(line => line.Length > 0))
            .Select(line => JsonDocument.Parse(line))
            .ToList();

        JsonElement FindLine(string sourceMessageId) => lines
            .Select(d => d.RootElement)
            .Single(e => e.GetProperty("source").GetProperty("source_message_id").GetString() == sourceMessageId);

        var exportedResolved = FindLine("sb:m2c_quote_resolved");
        Assert.Equal(
            StableIds.Message(directStableId, "s:954009715971000100"),
            exportedResolved.GetProperty("reply_to").GetProperty("message_id").GetString());
        Assert.Equal("上午十点评审。", exportedResolved.GetProperty("reply_to").GetProperty("text").GetString());

        var exportedUnresolved = FindLine("sb:m2c_quote_target_absent");
        Assert.True(exportedUnresolved.GetProperty("reply_to").GetProperty("message_id").ValueKind is JsonValueKind.Null);
        Assert.Equal("一条没有落库的引用", exportedUnresolved.GetProperty("reply_to").GetProperty("text").GetString());

        // Forwarded nesting survives the round-trip: structured items stay preserved with
        // their null per-item timestamps (E3 keeps them null; nothing is inferred).
        var groupStableId = StableIds.Conversation(
            accountStableId,
            ConversationKind.Group,
            SemanticBaselineCorpus.GroupConversation,
            null);
        var groupExportDirectory = Path.Combine(temp.Path, "export", SemanticBaselineCorpus.GroupConversation);
        var groupExport = await workflow.ExportConversationAsync(
            new ExportConversationRequest { ConversationId = groupStableId, OutputDirectory = groupExportDirectory },
            null,
            CancellationToken.None);
        Assert.True(groupExport.Succeeded);

        var groupLines = Directory
            .EnumerateFiles(groupExportDirectory, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadAllLines(path, System.Text.Encoding.UTF8).Where(line => line.Length > 0))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var bundle = groupLines.Single(e =>
            e.GetProperty("source").GetProperty("source_message_id").GetString() == "sb:bundle_group_structured");
        var items = bundle.GetProperty("payload").GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.All(items.EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("time").ValueKind));
        Assert.Equal("不支持类型的嵌套条目", items[2].GetProperty("text").GetString());

        // E2: the revoke payload keeps the derived newmsgid value when locally recorded and
        // stays null when the wire record carries none — the archive resolves neither.
        var revoke = groupLines.Single(e =>
            e.GetProperty("source").GetProperty("source_message_id").GetString() == "sb:revoke_group");
        Assert.Equal(
            StableIds.Message(groupStableId, "954009715970562000"),
            revoke.GetProperty("payload").GetProperty("revoked_message_id").GetString());
        var revokeWithoutId = groupLines.Single(e =>
            e.GetProperty("source").GetProperty("source_message_id").GetString() == "sb:m2c_revoke_without_newmsgid");
        Assert.Equal(JsonValueKind.Null, revokeWithoutId.GetProperty("payload").GetProperty("revoked_message_id").ValueKind);
    }

    // ---- pipeline harness ----------------------------------------------------------------

    private sealed record CorpusCounters(int Records, int Unknown, int Partial, int Unsupported);

    private static async Task<IReadOnlyList<ImportDiagnostic>> ImportCorpusAsync(string root)
    {
        var adapter = new SemanticBaselineSourceAdapter();
        var store = new SqliteArchiveStore(Path.Combine(root, "archive.db"), new FixedClock());
        var importer = new ImportService(adapter, store, new FixedClock());

        var diagnostics = new List<ImportDiagnostic>();
        foreach (var conversation in Conversations)
        {
            var peer = conversation.Kind == ConversationKind.Direct ? conversation.ConversationId : null;
            var outcome = await importer.ImportConversationAsync(new ImportRequest
            {
                SourceProfileId = SemanticBaselineCorpus.SourceProfileId,
                SourceConversationId = conversation.ConversationId,
                Kind = conversation.Kind,
                PeerSourceUserId = peer,
                ConversationTitle = "语义基线语料占位",
            }, null, CancellationToken.None);

            diagnostics.AddRange(outcome.Diagnostics);
        }

        return diagnostics;
    }

    private static async Task<(List<string> Lines, CorpusCounters Counters)> ImportAndExportAsync(string root)
    {
        var adapter = new SemanticBaselineSourceAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(Path.Combine(root, "archive.db"), clock);
        var importer = new ImportService(adapter, store, clock);
        var workflow = TestWorkflows.CreateExportWorkflow(new JsonlDatasetExporter(store), store, clock);

        var accountStableId = StableIds.Account(adapter.AdapterName, SemanticBaselineCorpus.SourceProfileId);
        foreach (var conversation in Conversations)
        {
            var peer = conversation.Kind == ConversationKind.Direct ? conversation.ConversationId : null;
            await importer.ImportConversationAsync(new ImportRequest
            {
                SourceProfileId = SemanticBaselineCorpus.SourceProfileId,
                SourceConversationId = conversation.ConversationId,
                Kind = conversation.Kind,
                PeerSourceUserId = peer,
                ConversationTitle = "语义基线语料占位",
            }, null, CancellationToken.None);
        }

        var records = 0;
        var unknown = 0;
        var partial = 0;
        var unsupported = 0;
        foreach (var conversation in Conversations)
        {
            var stableId = StableIds.Conversation(
                accountStableId,
                conversation.Kind,
                conversation.ConversationId,
                conversation.Kind == ConversationKind.Direct ? conversation.ConversationId : null);

            // Each conversation gets its own package root: an export manifest describes one
            // package, and a shared root would have each export overwrite the previous manifest.
            var outputDirectory = Path.Combine(root, conversation.ConversationId);
            var result = await workflow.ExportConversationAsync(
                new ExportConversationRequest { ConversationId = stableId, OutputDirectory = outputDirectory },
                null,
                CancellationToken.None);

            Assert.True(result.Succeeded);

            using var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json"), CancellationToken.None));
            records += manifest.RootElement.GetProperty("record_count").GetInt32();
            unknown += manifest.RootElement.GetProperty("unknown_count").GetInt32();
            partial += manifest.RootElement.GetProperty("partial_count").GetInt32();
            unsupported += manifest.RootElement.GetProperty("unsupported_count").GetInt32();
        }

        var lines = Directory
            .EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadAllLines(path, Encoding.UTF8).Where(line => line.Length > 0))
            .ToList();

        return (lines, new CorpusCounters(records, unknown, partial, unsupported));
    }
}
