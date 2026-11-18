using System.Text.RegularExpressions;
using NPOI.SS.UserModel;
using ZTools.Engine;

namespace ZTools.Offline;

public sealed record PumpSelectionValues(string Family, string Size, double FlowM3h, double HeadM, double NpshrM,
    double EfficiencyPercent, int Rpm);

public sealed partial class OfflineProject
{
    public object ApplyPumpSelection(int index, PumpSelectionValues pump)
    {
        if (pump.Family is not ("Z" or "CZ"))
            throw new ArgumentException("当前 ZTools 数据库仅支持 Z 系列和 CZ 系列选型导入。");
        if (index is < 0 or >= 30 || !Positive(pump.EfficiencyPercent) || pump.EfficiencyPercent > 100 ||
            !Positive(pump.FlowM3h) || !Positive(pump.HeadM) || !double.IsFinite(pump.NpshrM) || pump.NpshrM < 0 ||
            pump.Rpm <= 0)
            throw new ArgumentException("泵选型参数无效。");

        using var workbook = Book();
        var rowNumber = 21 + index;
        var model = NormalizeModel(workbook.Cell("input", "N" + rowNumber));
        var parts = Regex.Match(model, @"^(ZA|ZE|ZF|CZ)([GO]?)(\d+)-(\d{3,4})$");
        if (!parts.Success)
            throw new ArgumentException("请先选择一个有效的 ZTools 设备型号。");
        var familyCode = parts.Groups[1].Value;
        var suctionSize = parts.Groups[3].Value;
        var hydraulicCode = parts.Groups[4].Value;
        var nominalSize = suctionSize + "-" + (pump.Family == "Z" ? hydraulicCode[^3..] : hydraulicCode);
        if ((pump.Family == "CZ" ? familyCode != "CZ" : familyCode == "CZ") || NormalizeModel(pump.Size) != nominalSize)
            throw new ArgumentException("选型 " + pump.Family + " " + pump.Size + " 与当前设备 " + model + " 不匹配。");

        var engine = new SubmissionEngine(workbook, _ => {});
        var databaseModel = familyCode + suctionSize + "-" + hydraulicCode;
        engine.ValidatePumpDatabaseMatch(familyCode, databaseModel, index);

        var sheet = ((ExcelBook)workbook.Book).Data.GetSheet("input");
        var target = sheet.GetRow(rowNumber - 1);
        var values = new (int Column, double Value)[]
        {
            (11, pump.FlowM3h), (12, pump.HeadM), (15, pump.NpshrM),
            (16, pump.EfficiencyPercent / 100), (17, pump.Rpm)
        };
        var writes = values.Select(value => (Cell: RequireEditable(target, value.Column), value.Value)).ToArray();
        foreach (var write in writes) write.Cell.SetCellValue(write.Value);
        workbook.Save();
        needsCalculation = true;
        output = null;
        return State();
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static string NormalizeModel(string value) =>
        Regex.Replace(value ?? "", @"\s+", "").ToUpperInvariant();

    private static ICell RequireEditable(IRow row, int column)
    {
        var cell = row.GetCell(column) ?? row.CreateCell(column);
        if (cell.CellType == CellType.Formula)
            throw new InvalidOperationException("无法覆盖工作簿公式：" + cell.Address);
        return cell;
    }
}
