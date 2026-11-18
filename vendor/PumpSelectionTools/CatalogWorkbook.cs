using System.Globalization;
using NPOI.SS.UserModel;

namespace PumpSelectionTools;

internal static class CatalogWorkbook
{
    public static string Cell(ISheet sheet, int row, int column)
    {
        var cell = sheet.GetRow(row - 1)?.GetCell(column - 1);
        if (cell is null) return "";
        var type = cell.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell.CellType;
        return type switch
        {
            CellType.String => cell.StringCellValue,
            CellType.Numeric => cell.NumericCellValue.ToString("G15", CultureInfo.InvariantCulture),
            _ => ""
        };
    }
}
