namespace ZTools.Offline;

public sealed record SubmissionIssue(int? Row, string Equipment, string Group, string Field,
    string Address, string Message);

public sealed class SubmissionInputException(IReadOnlyList<SubmissionIssue> issues)
    : InvalidOperationException($"发现 {issues.Count} 项缺失或无效的参数，请更正后重新计算。")
{
    public IReadOnlyList<SubmissionIssue> Issues { get; } = issues;
}
