using System.Globalization;
using WeArchive.Core.Domain;

namespace WeArchive.App.ViewModels;

/// <summary>
/// Renders operation progress for the UI. Stages are the adapter-neutral stage names from
/// <see cref="Core.Abstractions.OperationStages"/>; the wording here is presentation only.
/// </summary>
public static class ProgressText
{
    public static string Describe(string stage) => stage switch
    {
        Core.Abstractions.OperationStages.ProbingSource => "正在检测微信数据源……",
        Core.Abstractions.OperationStages.ListingConversations => "正在枚举会话……",
        Core.Abstractions.OperationStages.ReadingMessages => "正在读取消息……",
        Core.Abstractions.OperationStages.Archiving => "正在写入本地档案……",
        Core.Abstractions.OperationStages.Exporting => "正在导出数据集……",
        Core.Abstractions.OperationStages.Completed => "已完成",
        Core.Abstractions.OperationStages.Cancelled => "已取消",
        Core.Abstractions.OperationStages.Failed => "失败",
        "writing_timeline" => "正在写出 JSONL……",
        _ => "处理中……",
    };

    public static string Counters(int processed, int total) =>
        total > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{processed} / {total}")
            : processed.ToString(CultureInfo.InvariantCulture);

    public static string DescribeSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Fatal => "致命",
        DiagnosticSeverity.Partial => "部分解析",
        _ => "信息",
    };

    public static string DescribeDiagnostic(ImportDiagnostic diagnostic)
    {
        var suffix = diagnostic.Count > 1
            ? string.Create(CultureInfo.InvariantCulture, $" ×{diagnostic.Count}")
            : string.Empty;

        var type = diagnostic.SourceType is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" (source_type={diagnostic.SourceType}/{diagnostic.SourceSubtype ?? "-"})");

        return $"[{DescribeSeverity(diagnostic.Severity)}] {diagnostic.Message}{suffix}{type}";
    }
}
