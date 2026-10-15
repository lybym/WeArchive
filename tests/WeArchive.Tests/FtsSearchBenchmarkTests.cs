using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WeArchive.Tests.Support;
using Xunit.Abstractions;

namespace WeArchive.Tests;

/// <summary>
/// M3b-0 FTS5 tokenization benchmark ([Issue #101](https://github.com/lybym/WeArchive/issues/101),
/// Roadmap M3b, PRD FR-15/16/17, HARNESS.md sections 6–7).
/// </summary>
/// <remarks>
/// <para>
/// The benchmark compares three locally-supported, deterministic FTS5 strategies over the same
/// privacy-safe fixture corpus (<see cref="SearchBenchmarkCorpus"/>) and pins the measured
/// evidence that <c>docs/SEARCH_SPEC.md</c> and ADR 0013 select from:
/// </para>
/// <list type="bullet">
/// <item><description><b>unicode61</b> — the FTS5 default tokenizer over the raw searchable text.</description></item>
/// <item><description><b>trigram</b> — the FTS5 trigram tokenizer over the raw searchable text.</description></item>
/// <item><description><b>bigram-projection</b> — unicode61 over the deterministic CJK bigram
/// projection specified by <c>docs/SEARCH_SPEC.md</c> ("Normalized index input").</description></item>
/// </list>
/// <para>
/// Product code is untouched: every strategy lives in this test assembly, so no FTS index,
/// migration or product behavior is introduced by the specification issue. The follow-up index
/// slice promotes the selected strategy into product code. Quality metrics are computed per seed
/// document against the matching-contract ground truth of <see cref="SearchBenchmarkCorpus"/>,
/// which makes them independent of the replica multiplier; latency and index size scale with the
/// replica count and are therefore recorded by the environment-gated scale test and quoted in the
/// specification.
/// </para>
/// </remarks>
public sealed class FtsSearchBenchmarkTests(ITestOutputHelper output)
{
    private const string ScaleEnvironmentVariable = "WEARCHIVE_SEARCH_BENCHMARK_SCALE";

    // --------------------------------------------------------------------------------------------
    // Deterministic projection (normative algorithm of docs/SEARCH_SPEC.md)
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void ProjectionFollowsTheSpecifiedBigramRules()
    {
        Assert.Equal(string.Empty, ChineseBigramProjection.Project(string.Empty));
        Assert.Equal("model card", ChineseBigramProjection.Project("model card"));
        Assert.Equal("模型", ChineseBigramProjection.Project("模型"));
        Assert.Equal("看", ChineseBigramProjection.Project("看"));
        Assert.Equal("大模 模型 型上 上线", ChineseBigramProjection.Project("大模型上线"));
        Assert.Equal("模型 型评 评估 估流 流程", ChineseBigramProjection.Project("模型评估流程"));
        Assert.Equal("下午 午三 三点 点开 开会 。", ChineseBigramProjection.Project("下午三点开会。"));
        Assert.Equal("AI 玩具", ChineseBigramProjection.Project("AI玩具"));
        Assert.Equal("新款 AI 玩具 具开 开卖 卖了", ChineseBigramProjection.Project("新款AI玩具开卖了"));
        Assert.Equal("GPT-5 API", ChineseBigramProjection.Project("GPT-5 API"));
        Assert.Equal("200.00", ChineseBigramProjection.Project("200.00"));
        Assert.Equal("200.00 元", ChineseBigramProjection.Project("200.00元"));
        Assert.Equal("华东 东中 中心 心项 项目 目汇 汇报 V8.pptx", ChineseBigramProjection.Project("华东中心项目汇报V8.pptx"));
        Assert.Equal("看 😀 模型", ChineseBigramProjection.Project("看😀模型"));
        Assert.Equal("㐀㐁", ChineseBigramProjection.Project("㐀㐁"));
        Assert.Equal("ｱｲｳ", ChineseBigramProjection.Project("ｱｲｳ"));
        // Determinism: the same input always projects to the same output.
        Assert.Equal(
            ChineseBigramProjection.Project("新款AI玩具开卖了，快看"),
            ChineseBigramProjection.Project("新款AI玩具开卖了，快看"));
    }

    [Fact]
    public void QueryParserProducesTheDocumentedTermStructureAndFailsClosed()
    {
        // Terms split on whitespace; implicit AND.
        var andTerms = SearchQueryParser.Parse("开会 预算");
        Assert.Equal(2, andTerms.Count);
        Assert.False(andTerms[0].IsPhrase);
        Assert.Equal("开会", andTerms[0].Content);

        // A quoted token is one phrase term with the quotes stripped.
        var phraseTerm = Assert.Single(SearchQueryParser.Parse("\"三点开会\""));
        Assert.True(phraseTerm.IsPhrase);
        Assert.Equal("三点开会", phraseTerm.Content);

        // Trailing * is a prefix marker, stripped from the content.
        var prefix = Assert.Single(SearchQueryParser.Parse("model*"));
        Assert.True(prefix.HasPrefixStar);
        Assert.Equal("model", prefix.Content);

        // A quote spans whitespace until its closing quote (or the end of the query), so a phrase
        // term may contain spaces.
        var spacedPhrase = Assert.Single(SearchQueryParser.Parse("\"AI 玩具\""));
        Assert.True(spacedPhrase.IsPhrase);
        Assert.Equal("AI 玩具", spacedPhrase.Content);

        // FTS5 operator characters stay literal: an unbalanced quote spans to the end of the
        // query, and every term is re-quoted by the compiler, so caller input can never become
        // FTS5 query syntax (the injection attempt stays one literal phrase token sequence).
        var injection = Assert.Single(SearchQueryParser.Parse("模型\") OR (1=1) --"));
        Assert.False(injection.IsPhrase);
        Assert.Equal("模型\") OR (1=1) --", injection.Content);

        // An unbalanced quote is an ordinary character inside its whitespace-delimited token.
        var unbalanced = Assert.Single(SearchQueryParser.Parse("\"三点开会"));
        Assert.Equal("\"三点开会", unbalanced.Content);

        // Fail-closed validation: no terms, or a term without any searchable character, is a
        // deterministic invalid query.
        Assert.Throws<SearchQueryInvalidException>(() => SearchQueryParser.Parse(string.Empty));
        Assert.Throws<SearchQueryInvalidException>(() => SearchQueryParser.Parse("   \t "));
        Assert.Throws<SearchQueryInvalidException>(() => SearchQueryParser.Parse("*"));
        Assert.Throws<SearchQueryInvalidException>(() => SearchQueryParser.Parse("!!!"));
        Assert.Throws<SearchQueryInvalidException>(() => SearchQueryParser.Parse("开会  !!!"));
    }

    [Fact]
    public void CorpusExpansionIsDeterministic()
    {
        const int multiplier = 37;
        var first = SearchBenchmarkCorpus.Expand(multiplier);
        var second = SearchBenchmarkCorpus.Expand(multiplier);

        Assert.Equal(SearchBenchmarkCorpus.Documents.Count * multiplier, first.Count);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i], second[i]);
        }

        // Every replica folds back onto its seed document and every seed is represented once per
        // replica slot, which is what makes the quality metrics scale-independent.
        Assert.Equal(
            SearchBenchmarkCorpus.Documents.Select(document => document.DocumentId).ToHashSet(),
            first.Select(document => SearchBenchmarkCorpus.SeedIdOf(document.DocumentId)).ToHashSet());
    }

    // --------------------------------------------------------------------------------------------
    // Pinned benchmark evidence (docs/SEARCH_SPEC.md section 11; ADR 0013)
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void BigramProjectionSatisfiesTheMatchingContractOnTheProbeSet()
    {
        using var strategy = new BigramProjectionSearchStrategy();
        var result = BenchmarkRunner.Run(strategy, SearchBenchmarkCorpus.Expand(1));

        Assert.Equal(1.0, result.MicroRecall);
        Assert.Equal(1.0, result.MicroPrecision);
        foreach (var probe in SearchBenchmarkCorpus.ProbeQueries)
        {
            var outcome = result.Outcome(probe.QueryId);
            Assert.True(
                outcome.RetrievedSeeds.SetEquals(probe.Relevant),
                $"{result.StrategyName} / {probe.QueryId} ('{probe.Query}'): expected [{string.Join(", ", probe.Relevant)}] got [{string.Join(", ", outcome.RetrievedSeeds)}]");
        }
    }

    [Fact]
    public void Unicode61AndTrigramCapabilitiesMatchThePinnedBenchmark()
    {
        var results = BenchmarkRunner.RunAll(SearchBenchmarkCorpus.Expand(1));
        var byName = results.ToDictionary(result => result.StrategyName);

        AssertPinnedMatchSets(byName["unicode61"], PinnedBenchmark.UniCode61);
        AssertPinnedMatchSets(byName["trigram"], PinnedBenchmark.Trigram);

        // The pinned gaps are exactly what the specification records: unicode61 loses CJK
        // substring/mixed recall; trigram loses every sub-trigram (typically 2-char) query.
        Assert.True(byName["unicode61"].MicroRecall < 1.0);
        Assert.True(byName["trigram"].MicroRecall < 1.0);
        Assert.Equal(1.0, byName["bigram-projection"].MicroRecall);
    }

    private void AssertPinnedMatchSets(
        BenchmarkResult result,
        IReadOnlyDictionary<string, IReadOnlySet<string>> pinned)
    {
        foreach (var (queryId, expected) in pinned)
        {
            var outcome = result.Outcome(queryId);
            Assert.True(
                outcome.RetrievedSeeds.SetEquals(expected),
                $"{result.StrategyName} / {queryId}: expected [{string.Join(", ", expected)}] got [{string.Join(", ", outcome.RetrievedSeeds)}]");
        }
    }

    [Fact]
    public void PinnedQualityMetricsMatchTheSpecificationTable()
    {
        var results = BenchmarkRunner.RunAll(SearchBenchmarkCorpus.Expand(1));
        foreach (var result in results)
        {
            output.WriteLine(
                $"{result.StrategyName}: micro_recall={result.MicroRecall:F6} micro_precision={result.MicroPrecision:F6} index_bytes={result.IndexBytes}");
            foreach (var category in result.Outcomes.Select(outcome => outcome.Category).Distinct())
            {
                output.WriteLine($"  recall[{category}]={result.CategoryRecall(category):F6}");
            }

            Assert.Equal(PinnedBenchmark.MicroRecall[result.StrategyName], result.MicroRecall, 12);
            Assert.Equal(PinnedBenchmark.MicroPrecision[result.StrategyName], result.MicroPrecision, 12);
            foreach (var (category, recall) in PinnedBenchmark.CategoryRecall[result.StrategyName])
            {
                Assert.Equal(recall, result.CategoryRecall(category), 12);
            }
        }
    }

    [Fact]
    public void IndexSizeOrderingMatchesTheBenchmarkFinding()
    {
        // Seed-scale index sizes are SQLite-page-quantized (4 KiB pages), so the ordering is
        // measured on a deterministic 6,000-document expansion where the differences are real.
        var results = BenchmarkRunner.RunAll(SearchBenchmarkCorpus.Expand(200));
        var byName = results.ToDictionary(result => result.StrategyName, result => result.IndexBytes);
        output.WriteLine($"index sizes: {string.Join(", ", byName.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value:N0}"))}");

        // Measured ordering (docs/SEARCH_SPEC.md section 11): the trigram index stores the most,
        // the projected bigram index grows over plain unicode61, and plain unicode61 is smallest.
        Assert.True(byName["trigram"] > byName["bigram-projection"], $"trigram={byName["trigram"]} bigram={byName["bigram-projection"]}");
        Assert.True(byName["bigram-projection"] > byName["unicode61"], $"bigram={byName["bigram-projection"]} unicode61={byName["unicode61"]}");
    }

    [Fact]
    public async Task RebuiltIndexIsDeterministic()
    {
        var first = BenchmarkRunner.Run(new BigramProjectionSearchStrategy(), SearchBenchmarkCorpus.Expand(1));
        var second = BenchmarkRunner.Run(new BigramProjectionSearchStrategy(), SearchBenchmarkCorpus.Expand(1));

        Assert.Equal(first.IndexBytes, second.IndexBytes);
        Assert.Equal(first.Outcomes.Count, second.Outcomes.Count);
        for (var i = 0; i < first.Outcomes.Count; i++)
        {
            Assert.Equal(first.Outcomes[i].QueryId, second.Outcomes[i].QueryId);
            Assert.Equal(first.Outcomes[i].RetrievedSeeds, second.Outcomes[i].RetrievedSeeds);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void IntegrityCheckPassesOnEveryStrategyIndex()
    {
        foreach (var strategy in BenchmarkRunner.CreateStrategies())
        {
            using (strategy)
            {
                strategy.Ingest(SearchBenchmarkCorpus.Expand(1));
                strategy.RunIntegrityCheck();
            }
        }
    }

    // --------------------------------------------------------------------------------------------
    // Failure modes (docs/SEARCH_SPEC.md "Failure modes")
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void InvalidQueriesFailClosedWithoutReachingFts()
    {
        using var strategy = new BigramProjectionSearchStrategy();
        strategy.Ingest(SearchBenchmarkCorpus.Expand(1));

        foreach (var invalid in new[] { string.Empty, "   ", "*", "!!!", "开会 !!!" })
        {
            Assert.Throws<SearchQueryInvalidException>(() => strategy.Query(invalid));
        }
    }

    [Fact]
    public void NoMatchQueriesReturnEmptyResultsNotErrors()
    {
        var results = BenchmarkRunner.RunAll(SearchBenchmarkCorpus.Expand(1));
        foreach (var result in results)
        {
            Assert.Empty(result.Outcome("q_none_cjk").RetrievedSeeds);
            Assert.Empty(result.Outcome("q_none_ascii").RetrievedSeeds);
        }
    }

    [Fact]
    public void ExcludedPayloadFieldsAreNeverIndexed()
    {
        var results = BenchmarkRunner.RunAll(SearchBenchmarkCorpus.Expand(1));
        foreach (var result in results)
        {
            Assert.Empty(result.Outcome("q_appid").RetrievedSeeds);
            Assert.Empty(result.Outcome("q_pagepath").RetrievedSeeds);
        }
    }

    // --------------------------------------------------------------------------------------------
    // Scale measurement (environment-gated; produces the tables quoted in docs/SEARCH_SPEC.md)
    // --------------------------------------------------------------------------------------------

    [SearchBenchmarkFact]
    public void ScaleBenchmarkProducesTheSpecificationTables()
    {
        var multiplier = int.Parse(
            Environment.GetEnvironmentVariable(ScaleEnvironmentVariable)!,
            System.Globalization.CultureInfo.InvariantCulture);
        var documents = SearchBenchmarkCorpus.Expand(multiplier);
        output.WriteLine($"corpus_documents={documents.Count} (multiplier={multiplier})");

        foreach (var result in BenchmarkRunner.RunAll(documents, measureLatency: true))
        {
            output.WriteLine(result.FormatScaleReport(documents.Count));
        }

        var rebuild = BenchmarkRunner.MeasureRebuild(new BigramProjectionSearchStrategy(), documents);
        output.WriteLine(
            $"bigram-projection rebuild: wall={rebuild.Wall.TotalSeconds:F2}s index_bytes={rebuild.IndexBytes:N0}");
    }

    // --------------------------------------------------------------------------------------------
    // Benchmark plumbing
    // --------------------------------------------------------------------------------------------

    private static class PinnedBenchmark
    {
        /// <summary>Pinned unicode61 match sets (seed document ids) per probe query.</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> UniCode61 = new Dictionary<string, IReadOnlySet<string>>
        {
            ["q_model_2"] = new HashSet<string>(),
            ["q_toy_2"] = new HashSet<string>(),
            ["q_meet_2"] = new HashSet<string>(),
            ["q_zhoubao_2"] = new HashSet<string>(),
            ["q_zhang_2"] = new HashSet<string>(["contact_card_zhang"]),
            ["q_ai_long"] = new HashSet<string>(),
            ["q_da_model"] = new HashSet<string>(),
            ["q_project_report"] = new HashSet<string>(),
            ["q_nanjing"] = new HashSet<string>(),
            ["q_yuhuatai"] = new HashSet<string>(),
            ["q_xiangmu"] = new HashSet<string>(),
            ["q_weishi"] = new HashSet<string>(),
            ["q_ai_toy"] = new HashSet<string>(),
            ["q_ai"] = new HashSet<string>(["text_ai_toy_english", "link_ai_report"]),
            ["q_search"] = new HashSet<string>(["link_standup"]),
            ["q_research"] = new HashSet<string>(["text_research_trap"]),
            ["q_index"] = new HashSet<string>(["link_standup"]),
            ["q_roadmap"] = new HashSet<string>(["text_q4_roadmap", "text_roadmap_review"]),
            ["q_q4"] = new HashSet<string>(["text_research_trap"]),
            ["q_gpt5"] = new HashSet<string>(["text_gpt_quota"]),
            ["q_api"] = new HashSet<string>(["text_gpt_quota", "text_api_docs"]),
            ["q_okr"] = new HashSet<string>(["text_okr_align", "text_okr_review"]),
            ["q_amount"] = new HashSet<string>(["transfer_paid", "text_price_thousand"]),
            ["q_huibao"] = new HashSet<string>(),
            ["q_pptx"] = new HashSet<string>(["file_report"]),
            ["q_huadong"] = new HashSet<string>(),
            ["q_zhouwu"] = new HashSet<string>(),
            ["q_jiage"] = new HashSet<string>(["text_price"]),
            ["q_phrase_meet"] = new HashSet<string>(),
            ["q_and_meet_budget"] = new HashSet<string>(),
            ["q_prefix_model"] = new HashSet<string>(["text_model_card", "text_models_sync"]),
            ["q_appid"] = new HashSet<string>(),
            ["q_pagepath"] = new HashSet<string>(),
            ["q_url"] = new HashSet<string>(["link_ai_report"]),
            ["q_none_cjk"] = new HashSet<string>(),
            ["q_none_ascii"] = new HashSet<string>(),
        };

        /// <summary>Pinned trigram match sets (seed document ids) per probe query.</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Trigram = new Dictionary<string, IReadOnlySet<string>>
        {
            ["q_model_2"] = new HashSet<string>(),
            ["q_toy_2"] = new HashSet<string>(),
            ["q_meet_2"] = new HashSet<string>(),
            ["q_zhoubao_2"] = new HashSet<string>(),
            ["q_zhang_2"] = new HashSet<string>(),
            ["q_ai_long"] = new HashSet<string>(["link_ai_report", "text_official_push"]),
            ["q_da_model"] = new HashSet<string>(["text_model_launch", "link_ai_report"]),
            ["q_project_report"] = new HashSet<string>(["file_report"]),
            ["q_nanjing"] = new HashSet<string>(["location_nanjing"]),
            ["q_yuhuatai"] = new HashSet<string>(["location_nanjing"]),
            ["q_xiangmu"] = new HashSet<string>(["forward_bundle_proj"]),
            ["q_weishi"] = new HashSet<string>(["text_unknown"]),
            ["q_ai_toy"] = new HashSet<string>(["text_ai_toy_sale"]),
            ["q_ai"] = new HashSet<string>(),
            ["q_search"] = new HashSet<string>(["link_standup", "text_research_trap"]),
            ["q_research"] = new HashSet<string>(["text_research_trap"]),
            ["q_index"] = new HashSet<string>(["link_standup"]),
            ["q_roadmap"] = new HashSet<string>(["text_q4_roadmap", "text_roadmap_review"]),
            ["q_q4"] = new HashSet<string>(),
            ["q_gpt5"] = new HashSet<string>(["text_gpt_quota"]),
            ["q_api"] = new HashSet<string>(["text_gpt_quota", "text_api_docs"]),
            ["q_okr"] = new HashSet<string>(["text_okr_align", "text_okr_review"]),
            ["q_amount"] = new HashSet<string>(["transfer_paid", "text_price", "text_price_thousand"]),
            ["q_huibao"] = new HashSet<string>(),
            ["q_pptx"] = new HashSet<string>(["file_report"]),
            ["q_huadong"] = new HashSet<string>(),
            ["q_zhouwu"] = new HashSet<string>(),
            ["q_jiage"] = new HashSet<string>(),
            ["q_phrase_meet"] = new HashSet<string>(["text_meeting_time"]),
            ["q_and_meet_budget"] = new HashSet<string>(),
            ["q_prefix_model"] = new HashSet<string>(["text_model_card", "text_models_sync"]),
            ["q_appid"] = new HashSet<string>(),
            ["q_pagepath"] = new HashSet<string>(),
            ["q_url"] = new HashSet<string>(["link_ai_report"]),
            ["q_none_cjk"] = new HashSet<string>(),
            ["q_none_ascii"] = new HashSet<string>(),
        };

        public static readonly IReadOnlyDictionary<string, double> MicroRecall = new Dictionary<string, double>
        {
            ["unicode61"] = 21.0 / 50.0,
            ["trigram"] = 28.0 / 50.0,
            ["bigram-projection"] = 1.0,
        };

        public static readonly IReadOnlyDictionary<string, double> MicroPrecision = new Dictionary<string, double>
        {
            ["unicode61"] = 1.0,
            ["trigram"] = 28.0 / 29.0,
            ["bigram-projection"] = 1.0,
        };

        public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> CategoryRecall =
            new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                ["unicode61"] = new Dictionary<string, double>
                {
                    ["cjk_2char"] = 1.0 / 11.0,
                    ["cjk_3char_plus"] = 0.0,
                    ["mixed"] = 2.0 / 4.0,
                    ["latin_word"] = 7.0 / 8.0,
                    ["punct_abbrev"] = 7.0 / 8.0,
                    ["partial_term"] = 2.0 / 6.0,
                    ["phrase"] = 0.0,
                    ["multi_term_and"] = 0.0,
                    ["prefix"] = 1.0,
                },
                ["trigram"] = new Dictionary<string, double>
                {
                    ["cjk_2char"] = 0.0,
                    ["cjk_3char_plus"] = 1.0,
                    ["mixed"] = 1.0 / 4.0,
                    ["latin_word"] = 6.0 / 8.0,
                    ["punct_abbrev"] = 1.0,
                    ["partial_term"] = 1.0 / 6.0,
                    ["phrase"] = 1.0,
                    ["multi_term_and"] = 0.0,
                    ["prefix"] = 1.0,
                },
                ["bigram-projection"] = new Dictionary<string, double>(),
            };
    }

    /// <summary>Runs every strategy over one corpus snapshot and keeps the per-strategy results.</summary>
    private static class BenchmarkRunner
    {
        public static List<FtsBenchmarkStrategy> CreateStrategies() =>
        [
            new Unicode61SearchStrategy(),
            new TrigramSearchStrategy(),
            new BigramProjectionSearchStrategy(),
        ];

        public static List<BenchmarkResult> RunAll(
            IReadOnlyList<SearchBenchmarkDocument> documents,
            bool measureLatency = false)
        {
            var results = new List<BenchmarkResult>();
            foreach (var strategy in CreateStrategies())
            {
                using (strategy)
                {
                    results.Add(Run(strategy, documents, measureLatency));
                }
            }

            return results;
        }

        public static BenchmarkResult Run(
            FtsBenchmarkStrategy strategy,
            IReadOnlyList<SearchBenchmarkDocument> documents,
            bool measureLatency = false)
        {
            strategy.Ingest(documents);
            strategy.RunIntegrityCheck();

            var outcomes = new List<ProbeOutcome>();
            var latencies = new Dictionary<string, double>();
            foreach (var probe in SearchBenchmarkCorpus.ProbeQueries)
            {
                var retrieved = strategy.Query(probe.Query);
                outcomes.Add(new ProbeOutcome(probe.QueryId, probe.Category, probe.Relevant, FoldToSeeds(retrieved)));
                if (measureLatency)
                {
                    latencies[probe.QueryId] = MeasureQueryLatencyMs(strategy, probe.Query);
                }
            }

            return new BenchmarkResult(
                strategy.Name,
                strategy.IndexBytes,
                strategy.IngestWall,
                strategy.IngestCpu,
                outcomes,
                latencies);
        }

        public static (TimeSpan Wall, long IndexBytes) MeasureRebuild(
            FtsBenchmarkStrategy strategy,
            IReadOnlyList<SearchBenchmarkDocument> documents)
        {
            using (strategy)
            {
                var clock = Stopwatch.StartNew();
                strategy.Ingest(documents);
                strategy.RunIntegrityCheck();
                clock.Stop();
                return (clock.Elapsed, strategy.IndexBytes);
            }
        }

        private static double MeasureQueryLatencyMs(FtsBenchmarkStrategy strategy, string query)
        {
            const int repetitions = 3;
            strategy.Query(query); // warmup
            var best = double.MaxValue;
            for (var i = 0; i < repetitions; i++)
            {
                var clock = Stopwatch.StartNew();
                strategy.Query(query);
                clock.Stop();
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        private static IReadOnlySet<string> FoldToSeeds(IReadOnlySet<string> documentIds) =>
            documentIds
                .Select(SearchBenchmarkCorpus.SeedIdOf)
                .ToHashSet();
    }

    /// <summary>Aggregate result of one strategy over the probe set.</summary>
    private sealed record BenchmarkResult(
        string StrategyName,
        long IndexBytes,
        TimeSpan IngestWall,
        TimeSpan IngestCpu,
        IReadOnlyList<ProbeOutcome> Outcomes,
        IReadOnlyDictionary<string, double> QueryLatencyMs)
    {
        private static double Ratio(int numerator, int denominator) =>
            denominator == 0 ? 1.0 : (double)numerator / denominator;

        public double MicroRecall
        {
            get
            {
                var (tp, fn) = Totals(SearchBenchmarkCorpus.RecallProbes);
                return Ratio(tp, tp + fn);
            }
        }

        public double MicroPrecision
        {
            get
            {
                var tp = 0;
                var fp = 0;
                foreach (var outcome in Outcomes)
                {
                    tp += outcome.TruePositives;
                    fp += outcome.FalsePositives;
                }

                return Ratio(tp, tp + fp);
            }
        }

        public double CategoryRecall(string category)
        {
            var (tp, fn) = Totals(SearchBenchmarkCorpus.RecallProbes.Where(probe => probe.Category == category));
            return Ratio(tp, tp + fn);
        }

        private (int TruePositives, int FalseNegatives) Totals(IEnumerable<SearchBenchmarkCorpus.ProbeQuery> probes)
        {
            int tp = 0, fn = 0;
            foreach (var probe in probes)
            {
                var outcome = Outcome(probe.QueryId);
                tp += outcome.TruePositives;
                fn += outcome.FalseNegatives;
            }

            return (tp, fn);
        }

        public ProbeOutcome Outcome(string queryId) =>
            Outcomes.First(outcome => outcome.QueryId == queryId);

        public double MedianLatencyMs()
        {
            var values = QueryLatencyMs.Values.OrderBy(value => value).ToArray();
            return values.Length == 0 ? 0 : values[values.Length / 2];
        }

        public string FormatScaleReport(int documentCount)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(
                $"{StrategyName}: index_bytes={IndexBytes:N0} ({IndexBytes / (double)documentCount:F0}/doc) " +
                $"ingest_wall={IngestWall.TotalSeconds:F2}s ingest_cpu={IngestCpu.TotalSeconds:F2}s " +
                $"median_query_ms={MedianLatencyMs():F3} micro_recall={MicroRecall:F4} micro_precision={MicroPrecision:F4}");
            foreach (var category in Outcomes.Select(outcome => outcome.Category).Distinct())
            {
                builder.Append($"\n  recall[{category}]={CategoryRecall(category):F4}");
            }

            foreach (var (queryId, latency) in QueryLatencyMs)
            {
                builder.Append($"\n  latency[{queryId}]={latency:F3}ms");
            }

            return builder.ToString();
        }
    }

    private sealed record ProbeOutcome(
        string QueryId,
        string Category,
        IReadOnlySet<string> Relevant,
        IReadOnlySet<string> RetrievedSeeds)
    {
        public int TruePositives => Relevant.Count(RetrievedSeeds.Contains);

        public int FalseNegatives => Relevant.Count(relevant => !RetrievedSeeds.Contains(relevant));

        public int FalsePositives => RetrievedSeeds.Count(retrieved => !Relevant.Contains(retrieved));
    }

    /// <summary>Deterministic invalid-query signal; the spec maps it to <c>query_invalid</c> (exit 2).</summary>
    public sealed class SearchQueryInvalidException(string message) : Exception(message);

    private readonly record struct SearchQueryTerm(string Content, bool IsPhrase, bool HasPrefixStar);

    /// <summary>
    /// Shared caller-query parsing: whitespace-separated terms, a token wrapped in double quotes is
    /// one phrase term, one trailing <c>*</c> marks a prefix term. Everything else stays literal —
    /// the compiler quotes every term, so caller input can never become FTS5 query syntax.
    /// </summary>
    private static class SearchQueryParser
    {
        public static IReadOnlyList<SearchQueryTerm> Parse(string query)
        {
            ArgumentNullException.ThrowIfNull(query);

            var rawTokens = SplitTokens(query);
            if (rawTokens.Count == 0)
                throw new SearchQueryInvalidException("the query is empty.");

            var terms = new List<SearchQueryTerm>(rawTokens.Count);
            foreach (var raw in rawTokens)
            {
                var content = raw;
                var isPhrase = false;
                if (content.Length >= 2 && content.StartsWith('"') && content.EndsWith('"'))
                {
                    isPhrase = true;
                    content = content[1..^1];
                }

                var hasPrefixStar = false;
                if (content.EndsWith('*'))
                {
                    hasPrefixStar = true;
                    content = content[..^1];
                }

                if (content.Length == 0)
                    throw new SearchQueryInvalidException("the query contains an empty term.");

                if (!content.Any(character => char.IsLetterOrDigit(character) || ChineseBigramProjection.IsCjkIdeograph(character)))
                    throw new SearchQueryInvalidException("the query contains a term without any searchable character.");

                terms.Add(new SearchQueryTerm(content, isPhrase, hasPrefixStar));
            }

            return terms;
        }

        private static List<string> SplitTokens(string query)
        {
            var tokens = new List<string>();
            var builder = new System.Text.StringBuilder();
            var inQuote = false;
            foreach (var character in query)
            {
                if (character == '"')
                {
                    inQuote = !inQuote;
                    builder.Append(character);
                }
                else if (char.IsWhiteSpace(character) && !inQuote)
                {
                    if (builder.Length > 0)
                    {
                        tokens.Add(builder.ToString());
                        builder.Clear();
                    }
                }
                else
                {
                    builder.Append(character);
                }
            }

            if (builder.Length > 0)
                tokens.Add(builder.ToString());

            return tokens;
        }

        public static string Quote(string content) =>
            "\"" + content.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// The CJK bigram projection specified by docs/SEARCH_SPEC.md: BMP CJK ideograph runs of two or
    /// more characters are rewritten to their overlapping bigrams joined by spaces (order
    /// preserved), single CJK ideographs and all other characters are copied verbatim. Applying the
    /// same projection to documents and query terms turns unicode61 token adjacency into exact CJK
    /// substring semantics.
    /// </summary>
    internal static class ChineseBigramProjection
    {
        public static bool IsCjkIdeograph(char character) =>
            character is >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

        public static string Project(string input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (input.Length == 0)
                return string.Empty;

            var builder = new System.Text.StringBuilder(input.Length + 8);
            var lastWasCjk = false;
            var index = 0;
            while (index < input.Length)
            {
                if (!IsCjkIdeograph(input[index]))
                {
                    AppendSeparated(builder, input[index], isCjk: false, ref lastWasCjk);
                    index++;
                    continue;
                }

                var runStart = index;
                while (index < input.Length && IsCjkIdeograph(input[index]))
                    index++;

                if (index - runStart == 1)
                {
                    AppendSeparated(builder, input[runStart], isCjk: true, ref lastWasCjk);
                }
                else
                {
                    // First bigram: boundary-aware first char, then its partner. Consecutive
                    // bigrams share one character, so each following bigram is "space + two chars".
                    AppendSeparated(builder, input[runStart], isCjk: true, ref lastWasCjk);
                    builder.Append(input[runStart + 1]);
                    for (var offset = 1; offset < index - runStart - 1; offset++)
                    {
                        builder.Append(' ');
                        builder.Append(input[runStart + offset]);
                        builder.Append(input[runStart + offset + 1]);
                    }

                    lastWasCjk = true;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Appends one character, inserting a separating space at every CJK↔non-CJK boundary.
        /// unicode61 treats ASCII letters/digits and CJK ideographs alike as token characters, so
        /// without this separator a projected bigram would glue to an adjacent Latin run (for
        /// example <c>汇报V8</c> or <c>200.00元</c> become single tokens) and break substring
        /// matching at the boundary. The inserted space is itself a unicode61 separator, so it
        /// never merges tokens and never changes within-class tokenization.
        /// </summary>
        private static void AppendSeparated(System.Text.StringBuilder builder, char character, bool isCjk, ref bool lastWasCjk)
        {
            if (builder.Length > 0 && isCjk != lastWasCjk)
                builder.Append(' ');
            builder.Append(character);
            lastWasCjk = isCjk;
        }
    }

    private abstract class FtsBenchmarkStrategy : IDisposable
    {
        private const string TableName = "search_fts";
        private readonly string _databasePath;
        private readonly SqliteConnection _connection;
        private readonly Func<string, string> _bodyTransform;

        protected FtsBenchmarkStrategy(string name, string tokenizerSql, Func<string, string> bodyTransform)
        {
            _databasePath = Path.Combine(Path.GetTempPath(), "wearchive-fts-bench-" + Guid.NewGuid().ToString("N") + ".db");
            _connection = new SqliteConnection($"Data Source={_databasePath}");
            _bodyTransform = bodyTransform;
            Name = name;
            TokenizerSql = tokenizerSql;
        }

        public string Name { get; }

        protected string TokenizerSql { get; }

        public TimeSpan IngestWall { get; private set; } = TimeSpan.Zero;

        public TimeSpan IngestCpu { get; private set; } = TimeSpan.Zero;

        public void Ingest(IReadOnlyList<SearchBenchmarkDocument> documents)
        {
            _connection.Open();
            using (var create = _connection.CreateCommand())
            {
                create.CommandText =
                    $"CREATE VIRTUAL TABLE {TableName} USING fts5(body, doc UNINDEXED, tokenize='{TokenizerSql}');";
                create.ExecuteNonQuery();
            }

            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            using (var transaction = _connection.BeginTransaction())
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO {TableName}(body, doc) VALUES ($body, $doc);";
                var bodyParameter = insert.Parameters.Add("$body", SqliteType.Text);
                var docParameter = insert.Parameters.Add("$doc", SqliteType.Text);
                insert.Prepare();
                foreach (var document in documents)
                {
                    bodyParameter.Value = _bodyTransform(SearchBenchmarkCorpus.SearchableText(document));
                    docParameter.Value = document.DocumentId;
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            clock.Stop();
            IngestWall = clock.Elapsed;
            IngestCpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        }

        public IReadOnlySet<string> Query(string userQuery)
        {
            var expression = BuildExpression(SearchQueryParser.Parse(userQuery));
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT doc FROM {TableName} WHERE {TableName} MATCH $expr ORDER BY rowid;";
            command.Parameters.AddWithValue("$expr", expression);
            var matches = new HashSet<string>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                matches.Add(reader.GetString(0));
            }

            return matches;
        }

        public void RunIntegrityCheck()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"INSERT INTO {TableName}({TableName}) VALUES ('integrity-check');";
            command.ExecuteNonQuery();
        }

        public long IndexBytes
        {
            get
            {
                using (var checkpoint = _connection.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    checkpoint.ExecuteNonQuery();
                }

                return new FileInfo(_databasePath).Length;
            }
        }

        protected abstract string BuildExpression(IReadOnlyList<SearchQueryTerm> terms);

        public void Dispose()
        {
            _connection.Dispose();
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // best-effort temp cleanup; the OS temp directory is reclaimed periodically
            }
        }
    }

    private sealed class Unicode61SearchStrategy()
        : FtsBenchmarkStrategy("unicode61", "unicode61", static body => body)
    {
        protected override string BuildExpression(IReadOnlyList<SearchQueryTerm> terms) =>
            string.Join(' ', terms.Select(term => SearchQueryParser.Quote(term.Content) + (term.HasPrefixStar ? "*" : string.Empty)));
    }

    private sealed class TrigramSearchStrategy()
        : FtsBenchmarkStrategy("trigram", "trigram", static body => body)
    {
        protected override string BuildExpression(IReadOnlyList<SearchQueryTerm> terms) =>
            string.Join(' ', terms.Select(term => SearchQueryParser.Quote(term.Content) + (term.HasPrefixStar ? "*" : string.Empty)));
    }

    private sealed class BigramProjectionSearchStrategy()
        : FtsBenchmarkStrategy("bigram-projection", "unicode61", static body => ChineseBigramProjection.Project(body))
    {
        protected override string BuildExpression(IReadOnlyList<SearchQueryTerm> terms) =>
            string.Join(' ', terms.Select(term => SearchQueryParser.Quote(ChineseBigramProjection.Project(term.Content)) + (term.HasPrefixStar ? "*" : string.Empty)));
    }

    /// <summary>Skips unless WEARCHIVE_SEARCH_BENCHMARK_SCALE names a positive multiplier.</summary>
    private sealed class SearchBenchmarkFactAttribute : FactAttribute
    {
        public SearchBenchmarkFactAttribute()
        {
            var raw = Environment.GetEnvironmentVariable(ScaleEnvironmentVariable);
            if (!int.TryParse(raw, out var multiplier) || multiplier <= 0)
                Skip = $"Set {ScaleEnvironmentVariable}=<docs-per-seed> to run the scale benchmark that produces the docs/SEARCH_SPEC.md tables.";
        }
    }
}
