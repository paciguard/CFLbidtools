using System.Globalization;
using System.IO.Compression;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using ZTools.Offline;

internal sealed record PumpDatasheetUpload(string? Content);
internal sealed record ImportedPumpDuty(double? Flow, double? Head, double Density, int Hz,
    double? ViscosityCp, double? Temperature, double? Npsha);
internal sealed record ImportedPumpRow(int SourceRow, ImportedPumpDuty Duty, PumpProjectDetails Details,
    string[] Issues);

internal static class PumpDatasheet
{
    // A base64 workbook at this limit fits the offline Android bridge as well.
    private const int MaxBytes = 360 * 1024;

    public static ImportedPumpRow[] Import(PumpDatasheetUpload upload)
    {
        if (string.IsNullOrEmpty(upload.Content) || upload.Content.Length > MaxBytes * 4 / 3)
            throw new ArgumentException("请选择不超过 360 KB 的选型一览表 .xlsx 文件。");
        byte[] data;
        try { data = Convert.FromBase64String(upload.Content); }
        catch (FormatException) { throw new ArgumentException("无法读取 Excel 文件内容。"); }

        using var input = new MemoryStream(data);
        XSSFWorkbook workbook;
        try
        {
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count > 256 || zip.Entries.Sum(entry => entry.Length) > 16 * 1024 * 1024)
                    throw new ArgumentException("Excel 文件解压后过大。请删除无关图片和工作表。");
            }
            input.Position = 0;
            workbook = new XSSFWorkbook(input);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or ArgumentException))
        {
            throw new ArgumentException("无法打开 .xlsx 工作簿。请确认文件完整且未加密。", error);
        }

        using (workbook)
        {
            var sheets = Enumerable.Range(0, workbook.NumberOfSheets).Select(workbook.GetSheetAt)
                .Where(IsTemplate).ToArray();
            if (sheets.Length != 1)
                throw new ArgumentException("工作簿必须包含一张选型一览表。请保留模板第 7–10 行的表头和单位。");
            var sheet = sheets[0];
            string customer = ReadText(sheet.GetRow(3)?.GetCell(0));
            var separator = customer.IndexOfAny(['：', ':']);
            customer = separator >= 0 ? customer[(separator + 1)..].Trim() :
                customer.Contains("consumer", StringComparison.OrdinalIgnoreCase) ? "" : customer;
            var result = new List<ImportedPumpRow>();
            for (int i = 10; i <= sheet.LastRowNum; i++)
            {
                var row = sheet.GetRow(i);
                if (ReadText(row?.GetCell(0)).StartsWith("配置说明", StringComparison.Ordinal)) break;
                // Ignore the template's numbered placeholders and calculated output cells.
                if (row == null || !Enumerable.Range(1, 30).Where(col => col is not (22 or 23 or 24 or 25 or 26))
                    .Any(col => ReadText(row.GetCell(col)).Length > 0)) continue;
                if (result.Count >= 30)
                    throw new ArgumentException("单次最多导入 30 个设备项，请拆分工作簿。");
                result.Add(ReadRow(row, customer));
            }
            if (result.Count == 0) throw new ArgumentException("模板中没有已填写的设备项。");
            return result.ToArray();
        }
    }

    private static bool IsTemplate(ISheet sheet)
    {
        bool Has(int row, int col, params string[] terms)
        {
            var value = string.Concat(ReadText(sheet.GetRow(row - 1)?.GetCell(col)).Where(c => !char.IsWhiteSpace(c)))
                .Replace("³", "3");
            return terms.All(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        return Has(7, 1, "位号") && Has(7, 2, "泵型号") && Has(7, 6, "数量") &&
            Has(8, 7, "流量", "m3/h") && Has(8, 8, "扬程", "m") && Has(8, 9, "温度", "℃") &&
            Has(8, 11, "Viscosity", "mPas") && Has(8, 12, "density", "kg/m3") &&
            Has(8, 13, "pressure", "MPaA") && Has(8, 15, "NPSHa", "NPSHr") &&
            Has(10, 9, "正常") && Has(10, 10, "最高") && Has(10, 13, "入口压力") && Has(10, 14, "出口压力") &&
            Has(10, 15, "a") && Has(10, 16, "r") && Has(9, 18, "冲洗方案") && Has(7, 30, "备注");
    }

    private static ImportedPumpRow ReadRow(IRow row, string customer)
    {
        string Read(int col) => ReadText(row.GetCell(col));
        var notes = new List<string>();
        var issues = new List<string>();
        double? Number(int col, string label, Func<double, bool> isValid, bool required = false)
        {
            var cell = row.GetCell(col);
            string text = Read(col);
            if (text.Length == 0)
            {
                if (required) issues.Add($"{label}：必填。");
                return null;
            }
            var number = cell != null && EffectiveType(cell) == CellType.Numeric
                ? cell.NumericCellValue
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
            if (double.IsFinite(number) &&
                (EffectiveType(cell!) != CellType.Numeric || !DateUtil.IsCellDateFormatted(cell)) &&
                isValid(number)) return number;
            issues.Add($"请核对{label}：{text}。");
            return null;
        }
        void Hint(string label, int col)
        {
            var value = Read(col);
            var cell = row.GetCell(col);
            if (col == 22 && cell != null && EffectiveType(cell) == CellType.Numeric &&
                cell.CellStyle.GetDataFormatString()?.Contains('%') == true)
                value = (cell.NumericCellValue * 100).ToString("G", CultureInfo.InvariantCulture);
            if (value.Length > 0 && value != "#ERROR") notes.Add(label + "：" + value);
        }

        var flow = Number(7, "流量 H（m³/h）", value => value >= .0001, required: true);
        var head = Number(8, "扬程 I（m）", value => value >= .0001, required: true);
        var temperature = Number(9, "正常温度 J（℃）", _ => true);
        var viscosity = Number(11, "黏度 L（mPa·s）", value => value >= .000001);
        var density = Number(12, "密度 M（kg/m³）", value => value >= .01);
        // The proposal template labels NPSHa separately from NPSHr but does not print the unit.
        var available = Number(15, "NPSHa P（请确认 m）", value => value >= 0);
        var quantity = (int?)Number(6, "数量 G", value => value is >= 1 and <= int.MaxValue && value == Math.Truncate(value));
        if (density == null) notes.Add("密度未录入有效值；选型界面暂用 1000 kg/m³。");
        if (available != null) notes.Add("原表 NPSHa 未印单位；当前按 m 导入，请确认。");
        notes.Add("原表未列电源频率；选型界面暂用 50 Hz。");
        Hint("原表泵型号", 2); Hint("原表泵形式", 5); Hint("最高温度（℃）", 10);
        Hint("入口绝压（MPa A）", 13); Hint("出口绝压（MPa A）", 14);
        Hint("原表 NPSHr（不用于选型）", 16); Hint("密封形式", 17); Hint("冲洗方案", 18);
        Hint("机械密封材质", 19); Hint("轴承架号", 20); Hint("过流部件材质", 21);
        Hint("原表效率（%）", 22); Hint("原表轴功率（kW）", 26);
        Hint("电机型号", 27); Hint("原表电机功率（kW）", 28); Hint("驱动及等级", 29); Hint("备注", 30);
        var material = Read(21);
        if (!System.Text.RegularExpressions.Regex.IsMatch(material, @"^[A-Z]-\d{1,2}$")) material = "";
        var tagCell = row.GetCell(1);
        var tag = tagCell?.CellType == CellType.Numeric
            ? new DataFormatter(CultureInfo.InvariantCulture).FormatCellValue(tagCell) : Read(1);
        var details = new PumpProjectDetails(customer, "", "", "", tag, Read(3), quantity, Read(4), material, "",
            notes.ToArray());
        return new(row.RowNum + 1, new(flow, head, density ?? 1000, 50, viscosity, temperature, available), details, issues.ToArray());
    }

    private static CellType EffectiveType(ICell cell) =>
        cell.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell.CellType;

    private static string ReadText(ICell? cell)
    {
        if (cell == null) return "";
        string value = EffectiveType(cell) switch
        {
            CellType.String => cell.StringCellValue.Trim(),
            CellType.Numeric => cell.NumericCellValue.ToString("G", CultureInfo.InvariantCulture),
            CellType.Boolean => cell.BooleanCellValue ? "TRUE" : "FALSE",
            CellType.Error => "#ERROR",
            _ => ""
        };
        return value.Length > 120 ? value[..120] : value;
    }
}
