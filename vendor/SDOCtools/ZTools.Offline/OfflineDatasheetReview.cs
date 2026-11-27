using NPOI.SS.UserModel;
using ZTools.Engine;

namespace ZTools.Offline;

public sealed partial class OfflineProject
{
    private const int ReviewColumn = 8, NotesColumn = 9;
    private const string Pending = "pending", Confirmed = "confirmed";

    private static IRow ProposalRow(IWorkbook book, int rowIndex) => book.GetSheet("CFL-App")?.GetRow(rowIndex);
    private static string ReviewStatus(IWorkbook book, int rowIndex) =>
        ProposalRow(book, rowIndex)?.GetCell(ReviewColumn)?.ToString();

    internal static bool NeedsDatasheetReview(IWorkbook book, int rowIndex) => ReviewStatus(book, rowIndex) == Pending;

    private static void StoreDatasheetReview(IWorkbook book, int rowIndex, string[] notes)
    {
        if (notes == null) return;
        if (notes.Length > 30 || notes.Any(note => note == null || note.Length > 240))
            throw new ArgumentException("设备原表备注过长。");
        var row = ProposalRow(book, rowIndex);
        RequireEditable(row, ReviewColumn).SetCellValue(Pending);
        RequireEditable(row, NotesColumn).SetCellValue(string.Join("\n", notes));
    }

    private static void InvalidateDatasheetReview(IWorkbook book, int rowIndex)
    {
        if (ReviewStatus(book, rowIndex) == Confirmed)
            RequireEditable(ProposalRow(book, rowIndex), ReviewColumn).SetCellValue(Pending);
    }

    private static void ConfirmDatasheetReview(WorkbookSession book, int item, InputChange[] changes)
    {
        var data = ((ExcelBook)book.Book).Data;
        int rowIndex = 20 + item;
        var status = ReviewStatus(data, rowIndex);
        if (item is < 0 or >= 30 || !HasEquipment(book, item) || status is not (Pending or Confirmed) ||
            changes is not [{ Sheet: "CFL-App", Value: Pending or Confirmed } review] || review.Address != "I" + (rowIndex + 1))
            throw new ArgumentException("设备确认记录无效。");
        RequireEditable(ProposalRow(data, rowIndex), ReviewColumn).SetCellValue(review.Value);
    }
}
