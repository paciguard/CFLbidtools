using System.Globalization;
using System.Text.RegularExpressions;
using NPOI.SS.UserModel;
using ZTools.Engine;

namespace ZTools.Offline;

public sealed partial class OfflineProject
{
    internal static readonly string[] MotorVoltages = { "380V", "6000V", "10000V" };

    internal static bool MotorSelectionFlag(IWorkbook book, int inputRow, int column) =>
        book.GetSheet("CFL-App")?.GetRow(inputRow - 1)?.GetCell(column) is
            { CellType: CellType.Boolean, BooleanCellValue: true };

    private static string[] MotorFrames(WorkbookSession book, int inputRow)
    {
        var match = ModelPattern.Match(NormalizeModel(book.Cell("input", "N" + inputRow)));
        if (!match.Success) return Array.Empty<string>();
        var family = match.Groups[1].Value;
        var model = family + match.Groups[3].Value + "-" + match.Groups[4].Value;
        var edition = ApiEdition.Number(book.Cell("input", "E" + inputRow));
        if (family == "ZE" && edition == 0) return Array.Empty<string>();
        var sheet = ((ExcelBook)book.Book).Data.GetSheet(family == "ZE" && edition == 7 ? "ZE7" : family);
        var voltage = book.Cell("input", "V" + inputRow);
        const string highVoltage = "(6KV)";
        return sheet.Where(row => row.RowNum >= 31 && row.GetCell(0)?.ToString() == model)
            .Select(row => row.GetCell(5)?.ToString().Trim())
            .Where(frame => !string.IsNullOrEmpty(frame) && (string.IsNullOrEmpty(voltage) ||
                frame.EndsWith(highVoltage, StringComparison.OrdinalIgnoreCase) == (voltage != "380V")))
            .Select(frame => frame.EndsWith(highVoltage, StringComparison.OrdinalIgnoreCase) ?
                frame[..^highVoltage.Length] : frame)
            .Distinct().ToArray();
    }

    private static object MotorValue(InputField field, string submitted)
    {
        var value = submitted?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 80) throw new ArgumentException("电机规格最多支持 80 个字符。");
        if (field.StrictChoices && !field.Choices.Contains(value))
            throw new ArgumentException("请选择有效的" + field.Label + "。");
        if (field.Sheet == "input" && field.Address.StartsWith('W'))
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kw) &&
                double.IsFinite(kw) && kw > 0) return kw;
            throw new ArgumentException("电机额定功率必须为正数 (kW)。");
        }
        if (field.Sheet == "CFL-App" && field.Address.StartsWith('E'))
        {
            value = value.ToUpperInvariant();
            if (!Regex.IsMatch(value, @"^IP[0-6X][0-9X]$"))
                throw new ArgumentException("请输入有效的 IP 防护等级，例如 IP55。");
        }
        return value;
    }
}
