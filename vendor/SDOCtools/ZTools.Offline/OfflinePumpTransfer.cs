using System.Text.RegularExpressions;
using NPOI.SS.UserModel;
using ZTools.Engine;

namespace ZTools.Offline;

public sealed record PumpSelectionValues(string Family, string Size, double FlowM3h, double HeadM, double NpshrM,
    double EfficiencyPercent, int Rpm, int Hz, double? MotorKw);

public sealed record PumpProjectDetails(string Client, string Project, string CustomerOrder, string Company,
    string Tag, string Service, int? Quantity, string Liquid, string Material, string DrawingModel);
public sealed record ProjectPump(PumpSelectionValues Pump, string Model, PumpProjectDetails Details, double? Temperature);

public sealed partial class OfflineProject
{
    private static void WriteProjectDetails(ISheet sheet, PumpProjectDetails details, double? temperature, int itemRow)
    {
        if (temperature.HasValue)
            RequireEditable(sheet.GetRow(itemRow - 1), 8).SetCellValue(temperature.Value);
        if (details == null) return;
        var entries = new (int, int, string)[]
        {
            (itemRow, 4, details.Tag), (itemRow, 6, details.Service), (itemRow, 8, details.Liquid),
            (itemRow, 15, details.Material), (itemRow, 28, details.DrawingModel)
        };
        if (itemRow == 21)
            entries = entries.Concat(new (int, int, string)[] {
                (10, 6, details.Client), (11, 6, details.Project), (12, 6, details.CustomerOrder), (10, 11, details.Company)
            }).ToArray();
        foreach (var (row, col, input) in entries)
        {
            if (string.IsNullOrWhiteSpace(input)) continue;
            var value = input.Trim();
            if (value.Length > 120) throw new ArgumentException("文字参数最多支持 120 个字符。");
            RequireEditable(sheet.GetRow(row - 1), col - 1).SetCellValue(value);
        }
        if (details.Quantity is int quantity)
        {
            if (quantity < 1) throw new ArgumentException("设备数量必须为正整数。");
            RequireEditable(sheet.GetRow(itemRow - 1), 6).SetCellValue(quantity);
        }
    }

    private static readonly Regex ModelPattern = new(@"^(ZA|ZE|ZF|CZ)([GO]?)(\d+)-(\d{3,4})$");
    private static readonly Lazy<string[]> CatalogModels = new(() =>
    {
        using var template = TemplateResources.Open("ZTools.xls");
        using var book = WorkbookFactory.Create(template);
        return new[] { "ZA", "ZE", "ZE7", "ZF", "CZ" }.SelectMany(sheet =>
                book.GetSheet(sheet).Where(row => row.RowNum is >= 31 and < 2000)
                    .Select(row => row.GetCell(0))
                    .Where(cell => cell?.CellType == CellType.String)
                    .Select(cell => NormalizeModel(cell.StringCellValue)))
            .Where(model => ModelPattern.IsMatch(model)).Distinct(StringComparer.Ordinal)
            .OrderBy(model => model, StringComparer.Ordinal).ToArray();
    });

    public static string[] PumpModels(PumpSelectionValues pump) =>
        pump.Family is "Z" or "CZ"
            ? CatalogModels.Value.Where(model => MatchesPump(ModelPattern.Match(model), pump)).ToArray()
            : Array.Empty<string>();

    private static bool MatchesPump(Match parts, PumpSelectionValues pump)
    {
        if (!parts.Success || (pump.Family == "CZ") != (parts.Groups[1].Value == "CZ")) return false;
        var code = parts.Groups[4].Value;
        var size = parts.Groups[3].Value + "-" + (pump.Family == "Z" ? code[^3..] : code);
        return NormalizeModel(pump.Size) == size;
    }

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
        var parts = ModelPattern.Match(model);
        if (!parts.Success)
            throw new ArgumentException("请先选择一个有效的 ZTools 设备型号。");
        var familyCode = parts.Groups[1].Value;
        var suctionSize = parts.Groups[3].Value;
        var hydraulicCode = parts.Groups[4].Value;
        if (!MatchesPump(parts, pump))
            throw new ArgumentException("选型 " + pump.Family + " " + pump.Size + " 与当前设备 " + model + " 不匹配。");

        if (new[] { "E", "U", "V" }.All(column => !string.IsNullOrWhiteSpace(workbook.Cell("input", column + rowNumber))))
        {
            var engine = new SubmissionEngine(workbook, _ => {});
            var databaseModel = familyCode + suctionSize + "-" + hydraulicCode;
            engine.ValidatePumpDatabaseMatch(familyCode, databaseModel, index);
        }

        WritePumpValues(((ExcelBook)workbook.Book).Data, rowNumber, pump);
        workbook.Save();
        needsCalculation = true;
        output = null;
        return State();
    }

    private static void WritePumpValues(IWorkbook book, int rowNumber, PumpSelectionValues pump)
    {
        var target = book.GetSheet("input").GetRow(rowNumber - 1);
        var app = book.GetSheet("CFL-App") ?? book.CreateSheet("CFL-App");
        var spec = app.GetRow(rowNumber - 1) ?? app.CreateRow(rowNumber - 1);
        var oldRpm = target.GetCell(17)?.ToString();
        var oldPower = target.GetCell(22)?.ToString();
        var frequency = RequireEditable(spec, 5);
        if (string.IsNullOrWhiteSpace(frequency.ToString())) frequency.SetCellValue(pump.Hz + " Hz");
        // B marks a pump-selection handoff; H records whether the motor rating came from it.
        RequireEditable(spec, 1).SetCellValue(true);
        RequireEditable(spec, 7).SetCellValue(pump.MotorKw.HasValue);
        var power = RequireEditable(target, 22);
        if (pump.MotorKw is double motorKw) power.SetCellValue(motorKw);
        else power.SetCellType(CellType.Blank);
        if (oldRpm != pump.Rpm.ToString() || oldPower != power.ToString())
        {
            RequireEditable(target, 18).SetCellType(CellType.Blank);
            RequireEditable(target, 20).SetCellType(CellType.Blank);
        }
        var values = new (int Column, double Value)[]
        {
            (11, pump.FlowM3h), (12, pump.HeadM), (15, pump.NpshrM),
            (16, pump.EfficiencyPercent / 100), (17, pump.Rpm)
        };
        var writes = values.Select(value => (Cell: RequireEditable(target, value.Column), value.Value)).ToArray();
        foreach (var write in writes) write.Cell.SetCellValue(write.Value);
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
