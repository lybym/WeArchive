namespace WeArchive.Tests.Support;

/// <summary>One searchable document shaped like a canonical archived message (Issue #101 benchmark).</summary>
/// <param name="DocumentId">Stable synthetic id; scale replicas suffix it with <c>#k</c>.</param>
/// <param name="ConversationId">Synthetic conversation bucket used only for replica spread.</param>
/// <param name="SemanticText">The canonical <c>semantic_text</c> the archive would store.</param>
/// <param name="PayloadFields">
/// Selected structured payload fields in canonical field order, already filtered to the
/// searchable-field inventory of <c>docs/SEARCH_SPEC.md</c>. Fields listed in
/// <see cref="ExcludedPayloadFields"/> are deliberately absent from this collection: the corpus
/// stores them on <see cref="ExcludedFieldValues"/> instead so the benchmark can prove that
/// excluded fields are never indexed.
/// </param>
public sealed record SearchBenchmarkDocument(
    string DocumentId,
    string ConversationId,
    string SemanticText,
    IReadOnlyList<KeyValuePair<string, string>> PayloadFields);

/// <summary>
/// Deterministic, privacy-safe Chinese/English/mixed fixture corpus and probe query set for the
/// M3b-0 FTS5 tokenization benchmark ([Issue #101](https://github.com/lybym/WeArchive/issues/101),
/// Roadmap M3b, PRD FR-15/16/17, HARNESS.md sections 6–7).
/// </summary>
/// <remarks>
/// <para>
/// The corpus is <b>not</b> an archive fixture: it is a set of documents shaped exactly like the
/// canonical searchable input specified by <c>docs/SEARCH_SPEC.md</c> (semantic text plus selected
/// structured payload fields), so the benchmark isolates tokenization economics from store
/// integration. The FTS5 index implementation itself is follow-up work; this corpus measures the
/// strategy that specification selects.
/// </para>
/// <para>
/// Privacy: every id, name, text, file name and URL is a synthetic placeholder in the style already
/// used by the repository docs and fixtures (张三/李四 role names, <c>example.com</c>, the
/// <c>华东中心项目汇报V8.pptx</c> example from MESSAGE_SCHEMA). No real conversation content,
/// archive, export, key or machine-private path is committed (docs/DEVELOPMENT.md section 8).
/// </para>
/// <para>
/// Ground truth is defined by the <b>matching contract</b> of <c>docs/SEARCH_SPEC.md</c>, not by
/// any one strategy's output: CJK substrings of two or more characters match inside longer runs;
/// Latin words match whole-word (case-insensitively), with an optional trailing <c>*</c> prefix
/// form; a quoted group matches its tokens adjacently; whitespace-separated terms combine with
/// AND; payload fields outside the searchable inventory never match. Recall/precision are computed
/// per seed document (a seed document counts as retrieved when any of its scale replicas is
/// retrieved), so the quality numbers are independent of the replica multiplier.
/// </para>
/// </remarks>
public static class SearchBenchmarkCorpus
{
    public const string DirectConversation = "bench_direct";
    public const string GroupConversation = "bench_group";
    public const string OfficialConversation = "bench_official";

    /// <summary>
    /// Payload fields that exist on real canonical payloads but are deliberately outside the
    /// searchable inventory (docs/SEARCH_SPEC.md "Searchable-field inventory"). The benchmark uses
    /// them to prove exclusions, never as index input.
    /// </summary>
    public static readonly string[] ExcludedPayloadFields =
    [
        "app_id",
        "page_path",
        "original_url",
        "fallback_url",
        "amount",
        "currency",
        "status",
        "duration_seconds",
        "size_bytes",
        "extension",
        "latitude",
        "longitude",
        "source_type",
        "source_subtype",
        "raw_summary",
    ];

    /// <summary>The 31 seed documents. Order is part of the determinism contract.</summary>
    public static IReadOnlyList<SearchBenchmarkDocument> Documents { get; } =
    [
        new("text_model_launch", DirectConversation,
            "大模型上线了，记得看周报", []),
        new("text_model_eval", DirectConversation,
            "模型评估流程已定，下周执行", []),
        new("text_ai_toy_sale", GroupConversation,
            "新款AI玩具开卖了，快看", []),
        new("text_toy_tidy", DirectConversation,
            "孩子的玩具收拾一下", []),
        new("text_ai_toy_english", GroupConversation,
            "AI toy unboxing review", []),
        new("text_meeting_time", DirectConversation,
            "下午三点开会。", []),
        new("text_meeting_budget", GroupConversation,
            "明天开会讨论预算，预算表随后发", []),
        new("text_budget_only", DirectConversation,
            "预算表已发邮件", []),
        new("file_report", DirectConversation,
            "[文件] 华东中心项目汇报V8.pptx",
            [new("filename", "华东中心项目汇报V8.pptx")]),
        new("link_ai_report", OfficialConversation,
            "[链接] 人工智能产业发展报告\nhttps://example.com/ai-report",
            [new("title", "人工智能产业发展报告"), new("description", "覆盖大模型与芯片产业")]),
        new("link_standup", DirectConversation,
            "standup notes: ship the search index this week", []),
        new("text_research_trap", GroupConversation,
            "research plan for Q4", []),
        new("text_gpt_quota", DirectConversation,
            "GPT-5 API 配额要调整", []),
        new("text_api_docs", DirectConversation,
            "新 API 文档已更新", []),
        new("text_okr_align", GroupConversation,
            "OKR 对齐会议改到周四", []),
        new("text_okr_review", DirectConversation,
            "q3 okr 复盘结束", []),
        new("transfer_paid", DirectConversation,
            "[转账] 200.00 元",
            []),
        new("text_price", GroupConversation,
            "价格：200.00元，含税", []),
        new("text_price_thousand", DirectConversation,
            "报价 1,200.00 美元", []),
        new("text_q4_roadmap", DirectConversation,
            "确认Q4 roadmap 后同步", []),
        new("text_roadmap_review", GroupConversation,
            "roadmap review moved to friday", []),
        new("text_model_card", DirectConversation,
            "model card 更新完毕", []),
        new("text_models_sync", GroupConversation,
            "sync models to storage", []),
        new("text_afternoon_plan", DirectConversation,
            "下午看模型评测结果", []),
        new("location_nanjing", DirectConversation,
            "[位置] 南京南站",
            [new("label", "南京南站"), new("address", "南京市雨花台区example路1号")]),
        new("contact_card_zhang", GroupConversation,
            "[联系人名片] 张三",
            [new("display_name", "张三")]),
        new("forward_bundle_proj", GroupConversation,
            "[合并转发] 项目讨论聊天记录，共 12 条",
            [new("title", "项目讨论聊天记录"),
             new("item_text_1", "第一版价格是不是太高了？"),
             new("item_text_2", "周五前给结论")]),
        new("text_unknown", DirectConversation,
            "[未识别消息]", []),
        new("text_voice", DirectConversation,
            "[语音]", []),
        new("text_official_push", OfficialConversation,
            "【Example日报】人工智能周报第12期", []),
        new("mini_program_shop", GroupConversation,
            "[小程序] XX商城 - 商品详情",
            [new("app_name", "XX商城"),
             new("title", "商品详情")]),
    ];

    /// <summary>Payload values that exist on the documents but must never be indexed.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ExcludedFieldValues { get; } =
    [
        new("app_id", "wx123456"),
        new("page_path", "pages/product?id=123"),
        new("amount", "200.00"),
        new("currency", "CNY"),
        new("status", "accepted"),
        new("extension", ".pptx"),
        new("original_url", "https://example.com/ai-report"),
        new("source_type", "49"),
        new("source_subtype", "101"),
        new("duration_seconds", "28"),
    ];

    /// <summary>One probe query with the matching-contract ground truth it must satisfy.</summary>
    /// <param name="QueryId">Stable probe id used by the pinned tests and the spec tables.</param>
    /// <param name="Query">The query exactly as a caller would type it.</param>
    /// <param name="Category">Benchmark category used by the per-category recall table.</param>
    /// <param name="Relevant">Ground-truth seed document ids under the SEARCH_SPEC contract.</param>
    public sealed record ProbeQuery(string QueryId, string Query, string Category, IReadOnlySet<string> Relevant);

    private static IReadOnlySet<string> Set(params string[] ids) => new HashSet<string>(ids);

    /// <summary>
    /// The 36 probe queries. Order is part of the determinism contract. Zero-relevance probes
    /// (exclusions and no-match) contribute false positives only and are excluded from recall
    /// denominators.
    /// </summary>
    public static IReadOnlyList<ProbeQuery> ProbeQueries { get; } =
    [
        // ---- CJK 2-char substring inside longer runs --------------------------------------------
        new("q_model_2", "模型", "cjk_2char",
            Set("text_model_launch", "text_model_eval", "text_afternoon_plan", "link_ai_report")),
        new("q_toy_2", "玩具", "cjk_2char", Set("text_ai_toy_sale", "text_toy_tidy")),
        new("q_meet_2", "开会", "cjk_2char", Set("text_meeting_time", "text_meeting_budget")),
        new("q_zhoubao_2", "周报", "cjk_2char", Set("text_model_launch", "text_official_push")),
        new("q_zhang_2", "张三", "cjk_2char", Set("contact_card_zhang")),
        // ---- CJK 3+ char substring ---------------------------------------------------------------
        new("q_ai_long", "人工智能", "cjk_3char_plus", Set("link_ai_report", "text_official_push")),
        new("q_da_model", "大模型", "cjk_3char_plus", Set("text_model_launch", "link_ai_report")),
        new("q_project_report", "项目汇报", "cjk_3char_plus", Set("file_report")),
        new("q_nanjing", "南京南", "cjk_3char_plus", Set("location_nanjing")),
        new("q_yuhuatai", "雨花台", "cjk_3char_plus", Set("location_nanjing")),
        new("q_xiangmu", "项目讨论", "cjk_3char_plus", Set("forward_bundle_proj")),
        new("q_weishi", "未识别", "cjk_3char_plus", Set("text_unknown")),
        // ---- mixed Chinese/Latin ------------------------------------------------------------------
        new("q_ai_toy", "AI玩具", "mixed", Set("text_ai_toy_sale")),
        // The projected token stream of link_ai_report contains the standalone word "ai" (its URL
        // is part of the canonical semantic text), so the matching contract returns it too.
        new("q_ai", "AI", "mixed", Set("text_ai_toy_sale", "text_ai_toy_english", "link_ai_report")),
        // ---- Latin whole-word (with substring trap) ------------------------------------------------
        new("q_search", "search", "latin_word", Set("link_standup")),
        new("q_research", "research", "latin_word", Set("text_research_trap")),
        new("q_index", "index", "latin_word", Set("link_standup")),
        new("q_roadmap", "roadmap", "latin_word", Set("text_q4_roadmap", "text_roadmap_review")),
        new("q_q4", "Q4", "latin_word", Set("text_q4_roadmap", "text_research_trap")),
        // ---- punctuation-bearing / abbreviations ---------------------------------------------------
        new("q_gpt5", "GPT-5", "punct_abbrev", Set("text_gpt_quota")),
        new("q_api", "API", "punct_abbrev", Set("text_gpt_quota", "text_api_docs")),
        new("q_okr", "OKR", "punct_abbrev", Set("text_okr_align", "text_okr_review")),
        new("q_amount", "200.00", "punct_abbrev",
            Set("transfer_paid", "text_price", "text_price_thousand")),
        // ---- partial terms inside filenames/payload fields ------------------------------------------
        new("q_huibao", "汇报", "partial_term", Set("file_report")),
        new("q_pptx", "pptx", "partial_term", Set("file_report")),
        new("q_huadong", "华东", "partial_term", Set("file_report")),
        new("q_zhouwu", "周五", "partial_term", Set("forward_bundle_proj")),
        new("q_jiage", "价格", "partial_term", Set("forward_bundle_proj", "text_price")),
        // ---- quoted phrase (adjacency escape hatch) ---------------------------------------------------
        new("q_phrase_meet", "\"三点开会\"", "phrase", Set("text_meeting_time")),
        // ---- whitespace-separated multi-term AND --------------------------------------------------------
        new("q_and_meet_budget", "开会 预算", "multi_term_and", Set("text_meeting_budget")),
        // ---- prefix --------------------------------------------------------------------------------------
        new("q_prefix_model", "model*", "prefix", Set("text_model_card", "text_models_sync")),
        // ---- excluded payload fields (never match; false positives only) ---------------------------------
        new("q_appid", "wx123456", "excluded_field", Set()),
        new("q_pagepath", "pages/product", "excluded_field", Set()),
        // ---- URL reachable through canonical semantic_text -------------------------------------------------
        new("q_url", "example.com", "latin_word", Set("link_ai_report")),
        // ---- no-match behavior (false positives only) -------------------------------------------------------
        new("q_none_cjk", "不存在的词组", "no_match", Set()),
        new("q_none_ascii", "zzqqxx", "no_match", Set()),
    ];

    /// <summary>Probe queries whose relevant set is non-empty; the recall denominator.</summary>
    public static IReadOnlyList<ProbeQuery> RecallProbes { get; } =
        [.. ProbeQueries.Where(probe => probe.Relevant.Count > 0)];

    /// <summary>
    /// Deterministically expands the seed corpus to <paramref name="multiplier"/> rows per seed
    /// document. Replica ids are <c>&lt;seed-id&gt;#&lt;k&gt;</c> so quality metrics can fold
    /// replicas back onto seed documents. The expansion is stable for a given multiplier.
    /// </summary>
    public static IReadOnlyList<SearchBenchmarkDocument> Expand(int multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(multiplier);
        if (multiplier <= 1)
            return Documents;

        string[] conversations = [DirectConversation, GroupConversation, OfficialConversation, "bench_group_2", "bench_direct_2"];
        var expanded = new List<SearchBenchmarkDocument>(Documents.Count * multiplier);
        foreach (var seed in Documents)
        {
            for (var replica = 0; replica < multiplier; replica++)
            {
                expanded.Add(replica == 0
                    ? seed
                    : seed with
                    {
                        DocumentId = $"{seed.DocumentId}#{replica}",
                        ConversationId = conversations[(replica + DocumentIdOrdinal(seed)) % conversations.Length],
                    });
            }
        }

        return expanded;
    }

    private static int DocumentIdOrdinal(SearchBenchmarkDocument document)
    {
        var ordinal = 0;
        foreach (var ch in document.DocumentId)
            ordinal = (ordinal * 31 + ch) & 0x7fffffff;
        return ordinal;
    }

    /// <summary>The seed document id of a (possibly replica) document id.</summary>
    public static string SeedIdOf(string documentId)
    {
        var hash = documentId.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? documentId : documentId[..hash];
    }

    /// <summary>The combined searchable text built from the document per the SEARCH_SPEC inventory.</summary>
    public static string SearchableText(SearchBenchmarkDocument document)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(document.SemanticText);
        foreach (var field in document.PayloadFields)
        {
            builder.Append('\n').Append(field.Value);
        }

        return builder.ToString();
    }
}
