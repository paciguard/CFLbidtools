using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using ZTools.Offline;
namespace ZTools.Engine;
internal sealed class WorkbookSession:IDisposable
{
 internal dynamic Application=>Book.Application;
 internal dynamic Book {get;}
 internal string FilePath {get;}
 private static readonly string[] RequiredSheets = { "Sheet1", "input", "PID", "Sec-List", "Utility", "UtilityData", "Motor", "CZ", "ZA", "ZE7", "ZE", "ZF", "nozzle-load", "Data1", "CZ-BearingSize", "List-all", "List-detail" };
 public WorkbookSession(string path,bool visible=false) {FilePath=Path.GetFullPath(path);if(Path.GetFileName(Path.GetDirectoryName(FilePath)).Equals("Ztools",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(FilePath))).Equals("Referrings",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请使用项目副本。");using var stream=File.OpenRead(FilePath);var book=new ExcelBook(stream,true);Book=book;foreach(var name in RequiredSheets){var sheet=Book.Worksheets[name];}if(book.Data.GetSheet("CFL-App")==null)book.Data.CreateSheet("CFL-App");}
 internal string Cell(string sheet,string address)=>Vb.Text(Book.Worksheets[sheet].Range[address].Value2);
 internal void Save()=>Book.Save(FilePath);
        internal int ValidateInput(bool utility = false)
        {
            var issues = new List<SubmissionIssue>();
            int count = 0;
            bool gap = false;
            var families = new[] { "CZ", "ZA", "ZE", "ZF" };
            var grades = Enumerable.Range(6, 11)
                .Select(col => (string)Vb.Text(Book.Worksheets["List-detail"].Cells[2, col].Value2))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int row = 21; row <= 50; row++)
            {
                string Read(string column) => Cell("input", column + row).Trim();
                string tag = Read("D"), model = Read("N");
                if (tag == "" && model == "")
                {
                    gap = true;
                    continue;
                }
                count++;
                string equipment = string.Join(" · ", new[] { tag, model }.Where(value => value != ""));
                void Add(string group, string column, string field, string message) =>
                    issues.Add(new(row - 21, equipment, group, field, "input!" + column + row, message));
                void Require(string group, string column, string field, string message)
                {
                    if (Read(column) == "") Add(group, column, field, message);
                }
                void Number(string group, string column, string field, Func<double,bool> valid, string range, bool required = true)
                {
                    string value = Read(column);
                    if (value == "")
                    {
                        if (required) Add(group, column, field, "请填写" + field + "。");
                    }
                    else if (!TryNumber(value, out double number) || !valid(number))
                        Add(group, column, field, range);
                }

                if (gap) Add("", "N", "设备行顺序", "设备之间有空行。请先补齐或删除前面的空行。");
                if (model == "") Add("hydraulics", "N", "泵型号", "请输入或从泵选型带入型号。");
                else
                {
                    string normalized = Regex.Replace(model, @"[\s?]", "").ToUpperInvariant();
                    if (!families.Any(family => normalized.StartsWith(family, StringComparison.Ordinal)))
                        Add("hydraulics", "N", "泵型号", "ZTools 仅支持 ZA、ZE、ZF 和 CZ 系列。");
                    else if (!Regex.IsMatch(normalized, @"^(ZA|ZE|ZF|CZ)[GO]?\d+-\d{3,4}$"))
                        Add("hydraulics", "N", "泵型号", "请输入完整型号，例如 ZA50-2250 或 CZ50-200。");
                }

                string flushing = Read("Y");
                string cooling = Read("Z");
                int planDigit = flushing.IndexOfAny("0123456789".ToCharArray());
                if (planDigit < 0 || flushing[planDigit..].Split('+').Any(string.IsNullOrWhiteSpace))
                    Add("configuration", "Y", "API 冲洗方案", "请输入有效的方案编号，例如 Plan 11 或 53B。");
                if (cooling is not ("" or "无" or "NO"))
                {
                    var parts = cooling.Split('+', StringSplitOptions.TrimEntries);
                    if (parts.Length > 4 || parts.Any(string.IsNullOrWhiteSpace))
                        Add("configuration", "Z", "冷却方案", "最多可填写 4 项方案，用 + 分隔；不冷却可填“无”。");
                    else if (flushing.Split('+').Length + parts.Length > 6)
                        Add("configuration", "Z", "冷却方案", "冲洗与冷却方案合计最多 6 项。");
                }
                if (utility) continue;

                Require("equipment", "D", "设备位号", "请填写设备位号。纯数字位号也可以使用。");
                Require("equipment", "F", "设备名称", "请填写设备名称或用途。");
                if (!grades.Contains(Read("O")) || Read("O") == "")
                    Add("equipment", "O", "材质等级", "请选择材料清单支持的等级，例如 S-5。");
                if (ApiEdition.Number(Read("E")) == 0)
                    Add("configuration", "E", "API 610 图纸版本", "请选择第 7、8 或 10 版。");
                Require("configuration", "AA", "轴承形式", "请选择与泵型号匹配的轴承形式。");
                Require("motor", "U", "机座 / GA 底座号", "请选择与电机及泵匹配的底座。");
                if (!OfflineProject.MotorVoltages.Contains(Read("V")))
                    Add("motor", "V", "电机铭牌电压", "请选择数据库支持的 380V、6000V 或 10000V。");
                Number("motor", "W", "电机额定功率 (kW)", value => value > 0, "请输入大于 0 的额定功率。");
                Number("hydraulics", "R", "泵转速 (rpm)", value => value > 0 && value == Math.Truncate(value), "请输入大于 0 的整数转速。");
                Number("hydraulics", "L", "流量 (m³/h)", value => value > 0, "请输入大于 0 的流量。");
                Number("hydraulics", "M", "扬程 (m)", value => value > 0, "请输入大于 0 的扬程。");
                Number("hydraulics", "P", "NPSHr (m)", value => value >= 0, "请输入大于或等于 0 的 NPSHr。", required: false);
                Number("hydraulics", "Q", "效率 (%)", value => value > 0 && value <= 1, "请在表单中输入 0 至 100 之间的效率（不含 0）。", required: false);
            }

            if (count == 0) issues.Add(new(null, "", "", "设备", "input!N21:N50", "请先添加设备或从泵选型创建项目。"));
            if (!utility)
            {
                if (!FlangeStandard.Choices.Contains(FlangeStandard.Normalize(Cell("input", "I53"))))
                    issues.Add(new(null, "", "project", "法兰标准", "input!I53", "请选择 " + string.Join("、", FlangeStandard.Choices) + "。"));
                if (!TryNumber(Cell("input", "I56"), out double factor) || factor <= 0)
                    issues.Add(new(null, "", "project", "管口载荷系数", "input!I56", "请输入大于 0 的载荷系数。"));
            }
            if (issues.Count != 0) throw new SubmissionInputException(issues);
            return count;
        }

        private static bool TryNumber(string value, out double number) =>
            (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
             double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) && double.IsFinite(number);

 public void Dispose()=>((ExcelBook)Book).Dispose();
}
