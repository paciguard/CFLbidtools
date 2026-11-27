using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using NPOI.SS.Util;
using ZTools.Engine;
using ZTools.Standalone;
namespace ZTools.Offline;

public sealed record EquipmentRow(int Index,string Tag,string Model,string Material,string Template,string List,bool ReviewPending=false);
public sealed record InputField(string Sheet,string Address,string Label,string Value,string[] Choices,bool ReadOnly=false,bool StrictChoices=false);
public sealed record ParameterForm(string Title,List<InputField> Fields);
public sealed record InputChange(string Sheet,string Address,string Value);
public sealed record ParameterChanges(string Group, int Row, InputChange[] Changes);
public sealed record ProjectEntry(string Id,string Name);
public sealed record OutputEntry(int Id,string Name,string Kind);
public sealed record OfflineState(string Job,bool NeedsCalculation,EquipmentRow[] Rows,SheetOptions Options,OutputEntry[] Files,string[] Log,bool Offline=true);

// Native hosts call this directly: no listener, Excel process or remote service.
public sealed partial class OfflineProject
{
    readonly string storage;readonly ConcurrentQueue<string> log=new();string workbook,output;bool needsCalculation=true;
    public OfflineProject(string storage){this.storage=Path.GetFullPath(storage);Directory.CreateDirectory(this.storage);System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);}
    public string Workbook=>workbook;
    void Report(string message){log.Enqueue(message);while(log.Count>100)log.TryDequeue(out _);}
    public object Progress()=>new{log=log.ToArray()};
    static string ProjectFile(string folder)=>System.IO.File.Exists(Path.Combine(folder,"ZTools.xlsx"))?Path.Combine(folder,"ZTools.xlsx"):Path.Combine(folder,"ZTools.xls");
    public ProjectEntry[] Projects()=>Directory.GetDirectories(storage).Where(d=>System.IO.File.Exists(ProjectFile(d))).OrderDescending().Select(d=>new ProjectEntry(Path.GetFileName(d),Path.GetFileName(d))).ToArray();
    public object Create(PumpSelectionValues pump = null, string model = null,
        PumpProjectDetails details = null, double? temperature = null)
        => CreateProject(pump == null ? Array.Empty<ProjectPump>() : new[] { new ProjectPump(pump, model, details, temperature) });

    public object CreateBatch(ProjectPump[] pumps)
    {
        if (pumps.Length is < 1 or > 30) throw new ArgumentException("批量项目须包含 1 至 30 台设备。");
        return CreateProject(pumps);
    }

    private object CreateProject(ProjectPump[] pumps)
    {
        for (int index = 0; index < pumps.Length; index++)
            if (!PumpModels(pumps[index].Pump).Contains(pumps[index].Model, StringComparer.Ordinal))
                throw new ArgumentException($"第 {index + 1} 项：请从匹配的 ZTools 型号中选择。");
        string dir=Path.Combine(storage,"项目-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ZTools.xlsx");
        ProjectWorkbook.Create(path, book =>
        {
            var input = book.GetSheet("input");
            for (int index = 0; index < pumps.Length; index++)
            {
                var item = pumps[index];
                RequireEditable(input.GetRow(20 + index), 13).SetCellValue(item.Model);
                WritePumpValues(book, 21 + index, item.Pump);
                WriteProjectDetails(input, item.Details, item.Temperature, 21 + index);
                StoreDatasheetReview(book, 20 + index, item.Details?.ProposalNotes);
            }
        });
        workbook = path;
        Reset();Report("项目已创建。");return State();
    }
    public object Open(string id)
    {
        if(!Projects().Any(p=>p.Id==id))throw new ArgumentException("项目不存在。");
        workbook=ProjectFile(Path.Combine(storage,id));
        if(Path.GetExtension(workbook).Equals(".xls",StringComparison.OrdinalIgnoreCase))
        {
            using var input=System.IO.File.OpenRead(workbook);using var source=NPOI.SS.UserModel.WorkbookFactory.Create(input);using var model=ProjectWorkbook.Materialize(source);
            string converted=Path.ChangeExtension(workbook,".xlsx");ProjectWorkbook.WriteInput(model,converted);workbook=converted;
        }
        Reset();return State();
    }
    public object Import(Stream input)
    {
        string dir=Path.Combine(storage,"导入-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(dir);
        string file=Path.Combine(dir,"ZTools.xlsx");using(var copy=System.IO.File.Create(file)){byte[] buffer=new byte[81920];int count;long total=0;while((count=input.Read(buffer))>0){total+=count;if(total>20*1024*1024)throw new InvalidOperationException("项目文件不能超过 20 MB。");copy.Write(buffer,0,count);}}
        using(var validation=new WorkbookSession(file)){validation.Save();}workbook=file;Reset();return State();
    }
    void Reset(){output=null;needsCalculation=true;log.Clear();}
    public object AddEquipment()
    {
        using(var b=Book())
        {
            int count=0;while(count<30&&HasEquipment(b,count))count++;
            if(count==30)throw new InvalidOperationException("每个项目最多支持 30 台设备。");
            b.Book.Worksheets["input"].Range["D"+(21+count)].Value2="设备-"+(count+1);b.Save();
        }
        needsCalculation=true;output=null;return State();
    }
    public object RemoveEquipment(int index)
    {
        if(index<0||index>=30)throw new ArgumentOutOfRangeException(nameof(index));
        using(var b=Book())
        {
            if(!HasEquipment(b,index))throw new ArgumentException("设备不存在。");
            var current=(ExcelBook)b.Book;int last=index;
            while(last<29&&HasEquipment(b,last+1)){CopyEquipment(current,last+1,current,last);last++;}
            foreach (var (sheet, offset, first, end) in EquipmentRanges)
                ProjectWorkbook.ClearInputs(current.Data.GetSheet(sheet), offset+last, offset+last, first, end);
            current.Evaluator.ClearAllCachedResultValues();b.Save();
        }
        needsCalculation=true;output=null;return State();
    }
    private static readonly (string Sheet, int Offset, int Start, int End)[] EquipmentRanges =
        { ("input",21,4,31), ("PID",10,6,9), ("Sec-List",10,6,15), ("Utility",13,5,11), ("CFL-App",21,2,10) };

    static bool HasEquipment(WorkbookSession book, int index) =>
        !string.IsNullOrWhiteSpace(book.Cell("input", "N" + (21 + index))) ||
        !string.IsNullOrWhiteSpace(book.Cell("input", "D" + (21 + index)));

    static void CopyEquipment(ExcelBook source,int from,ExcelBook target,int to)
    {
        foreach(var (sheet,offset,start,end) in EquipmentRanges)
        for(int col=start;col<=end;col++)
        {
            var src=source.Data.GetSheet(sheet).GetRow(offset+from-1)?.GetCell(col-1);var dest=target.Data.GetSheet(sheet).GetRow(offset+to-1)?.GetCell(col-1);
            if(src?.CellType==NPOI.SS.UserModel.CellType.Formula||dest?.CellType==NPOI.SS.UserModel.CellType.Formula)continue;
            target.Worksheets[sheet].Cells[offset+to,col].Value2=source.Value(src);
        }
    }
    WorkbookSession Book()=>new(workbook??throw new InvalidOperationException("请先新建或打开项目。"));
    public object State()
    {
        if(workbook!=null){using var book=Book();return Snapshot(book);}
        return new OfflineState(null,needsCalculation,Array.Empty<EquipmentRow>(),new(),Files(),log.ToArray());
    }
    OfflineState Snapshot(WorkbookSession b)
    {
        var rows=new List<EquipmentRow>();SheetOptions options=new();
        for(int i=0;i<30;i++)if(HasEquipment(b,i))rows.Add(new(i,b.Cell("input","D"+(21+i)),b.Cell("input","N"+(21+i)),b.Cell("input","O"+(21+i)),b.Cell("Sheet1","CA"+(40+i)),b.Cell("Sheet1","CC"+(40+i)),NeedsDatasheetReview(((ExcelBook)b.Book).Data,20+i)));
        options=ReadOptions(b);
        return new OfflineState(Path.GetFileName(Path.GetDirectoryName(workbook)),needsCalculation,rows.ToArray(),options,Files(),log.ToArray());
    }
    public ParameterForm Parameters(string group,int index)
    {
        using var b=Book();
        return Parameters(group,index,b);
    }

    private ParameterForm Parameters(string group,int index,WorkbookSession b)
    {
        if(index<0||index>=30)throw new ArgumentOutOfRangeException(nameof(index));var fields=new List<InputField>();
        void Field(string sheet,string address,string label){var point=new CellReference(address);var source=((ExcelBook)b.Book).Data.GetSheet(sheet).GetRow(point.Row)?.GetCell(point.Col);bool readOnly=source?.CellType==NPOI.SS.UserModel.CellType.Formula&&!(sheet=="Sec-List"&&(point.Col==5||point.Col==13));string value=b.Cell(sheet,address);if(source!=null&&(source.CellType==NPOI.SS.UserModel.CellType.Numeric||(source.CellType==NPOI.SS.UserModel.CellType.Formula&&source.CachedFormulaResultType==NPOI.SS.UserModel.CellType.Numeric))&&NPOI.SS.UserModel.DateUtil.IsCellDateFormatted(source))value=((ExcelBook)b.Book).Formatter.FormatCellValue(source,((ExcelBook)b.Book).Evaluator);fields.Add(new(sheet,address,string.IsNullOrWhiteSpace(label)?address:label,value,Choices((ExcelBook)b.Book,sheet=="Sec-List"&&(point.Col==5||point.Col==13)?"input":sheet,sheet=="Sec-List"&&(point.Col==5||point.Col==13)?(point.Col==5?"O":"AA")+(point.Row+12):address),readOnly));}
        int row = 21 + index;
        var data = ((ExcelBook)b.Book).Data;
        bool fromSelection = MotorSelectionFlag(data, row, 1);
        bool fixedPower = MotorSelectionFlag(data, row, 7);
        void Edition()
        {
            Field("input", "E" + row, "API 610 图纸版本");
            var field = fields[^1];
            var edition = ApiEdition.Number(field.Value);
            fields[^1] = field with
            {
                Value = edition == 0 ? field.Value : edition + "版",
                Choices = ApiEdition.Choices, StrictChoices = true
            };
        }
        string title;
        switch(group)
        {
            case "project":
                title = "项目与计算设置";
                Field("input", "L12", "设计");
                Field("input", "L13", "审核");
                Field("Sheet1", "C6", "日期");
                Field("Sheet1", "C10", "CFL订单号");
                Field("input", "F10", "客户名称");
                Field("input", "F11", "项目名称");
                Field("input", "F12", "客户订单号");
                Field("input", "K10", "图纸公司名称");
                Field("input", "I53", "法兰标准");
                fields[^1] = fields[^1] with
                {
                    Value = FlangeStandard.Normalize(fields[^1].Value),
                    Choices = FlangeStandard.Choices, StrictChoices = true
                };
                Field("input", "I56", "管口载荷系数");
                break;
            case "equipment":
                title = "设备参数";
                foreach (string col in new[]{"D","F","G","H","I","J","K","L","M","N","O","P","Q","R","X","AC","AD"})
                    Field("input", col + row, b.Cell("input", col + "19"));
                Field("input", "AB" + row, "图纸型号（前缀或完整型号）");
                if (fromSelection)
                    fields[fields.FindIndex(field => field.Address == "R" + row)] =
                        fields.First(field => field.Address == "R" + row) with { ReadOnly = true };
                break;
            case "configuration":
                title = "API 版本、轴承与方案";
                Edition();
                Field("input", "AA" + row, "轴承形式");
                if (NormalizeModel(b.Cell("input", "N" + row)).StartsWith("CZ", StringComparison.Ordinal))
                    fields[^1] = fields[^1] with { Choices = new[] { "CZ轻轴", "CZ重轴" }, StrictChoices = true };
                Field("input", "Y" + row, "API 冲洗方案");
                Field("input", "Z" + row, "冷却方案");
                fields[^1] = fields[^1] with
                {
                    Choices = new[] { "无" }.Concat(Enumerable.Range(8, 26)
                        .Select(r => b.Cell("UtilityData", "D" + r))
                        .Where(value => !string.IsNullOrWhiteSpace(value))).Distinct().ToArray()
                };
                break;
            case "hydraulics":
                title = "泵型与运行参数";
                Field("input", "N" + row, "泵型号");
                Field("input", "L" + row, "流量 (m³/h)");
                Field("input", "M" + row, "扬程 (m)");
                Field("input", "P" + row, "NPSHr (m)");
                Field("input", "Q" + row, "效率 (%)");
                Field("input", "R" + row, "泵转速 (rpm)");
                if (fromSelection) fields[^1] = fields[^1] with { ReadOnly = true };
                break;
            case "motor":
                title = "电机规格与底座";
                Field("input", "R" + row, "泵选型转速 (rpm)");
                fields[^1] = fields[^1] with { ReadOnly = true };
                Field("input", "W" + row, fixedPower ? "选型推荐电机功率 (kW)" : "电机额定功率 (kW)");
                fields[^1] = fields[^1] with { ReadOnly = fixedPower };
                Field("CFL-App", "C" + row, "电机系列 / 能效系列");
                fields[^1] = fields[^1] with { Choices = new[] { "YBX3", "YBX4", "YBX5" } };
                Field("CFL-App", "D" + row, "防爆标志");
                Field("CFL-App", "E" + row, "外壳防护等级 (IP)");
                fields[^1] = fields[^1] with { Choices = new[] { "IP55", "IP56", "IP65", "IP66" } };
                Field("input", "T" + row, "电机品牌 / 制造商");
                Field("CFL-App", "F" + row, "电机铭牌频率");
                fields[^1] = fields[^1] with { Choices = new[] { "50 Hz", "60 Hz" }, StrictChoices = true };
                Field("CFL-App", "G" + row, "驱动方式");
                fields[^1] = fields[^1] with { Choices = new[] { "工频", "VFD" }, StrictChoices = true };
                Field("input", "V" + row, "电机铭牌电压");
                fields[^1] = fields[^1] with { Choices = MotorVoltages, StrictChoices = true };
                Field("input", "U" + row, "机座 / GA 底座号");
                var frames = MotorFrames(b, row);
                if (frames.Length != 0) fields[^1] = fields[^1] with { Choices = frames };
                Field("input", "S" + row, "制造商完整型号（可选）");
                break;
            case "pid":title="管仪参数";foreach(string col in new[]{"F","G","H","I"})Field("PID",col+(10+index),b.Cell("PID",col+"8"));foreach(string col in new[]{"Y","Z"})Field("input",col+(21+index),b.Cell("input",col+"19"));break;
            case "materials":title="剖面与材料清单参数";foreach(string col in new[]{"F","G","H","I","J","K","L","M","N","O"})Field("Sec-List",col+(10+index),col=="O"?"轴承架材质（330.01 / 330.02 / 330.03；360.01 / 360.02）":b.Cell("Sec-List",col+"8"));break;
            case "utility":title="公用工程参数";foreach(string col in new[]{"E","F","G","H","I","J","K"})Field("Utility",col+(13+index),b.Cell("Utility",col+"12"));break;
            default:throw new ArgumentException("未知参数窗口。");
        }
        return new(title,fields);
    }
    static string[] Choices(ExcelBook book,string sheet,string address)
    {
        var cell=new CellReference(address);
        IList<NPOI.SS.UserModel.IDataValidation> validations;
        try{validations=book.Data.GetSheet(sheet).GetDataValidations();}
        catch(InvalidOperationException error)when(error.Message.StartsWith("Unexpected (UnknownRecord) while looking for DV Table insert pos",StringComparison.Ordinal)){return Array.Empty<string>();}
        var validation=validations.FirstOrDefault(v=>v.Regions.CellRangeAddresses.Any(a=>a.IsInRange(cell.Row,cell.Col)));
        var constraint=validation?.ValidationConstraint;if(constraint==null)return Array.Empty<string>();
        if(constraint.ExplicitListValues?.Length>0)return constraint.ExplicitListValues;
        string formula=constraint.Formula1?.TrimStart('=');if(string.IsNullOrEmpty(formula))return Array.Empty<string>();
        var named=book.Data.GetName(formula);if(named!=null)formula=named.RefersToFormula;
        if(!System.Text.RegularExpressions.Regex.IsMatch(formula,@"^(?:'[^']+'|[\w-]+)!\$?[A-Z]+\$?\d+:\$?[A-Z]+\$?\d+$|^\$?[A-Z]+\$?\d+:\$?[A-Z]+\$?\d+$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return Array.Empty<string>();
        var area=new AreaReference(formula,NPOI.SS.SpreadsheetVersion.EXCEL97);var values=new List<string>();string target=area.FirstCell.SheetName??sheet;
        foreach(var reference in area.GetAllReferencedCells()){var value=book.Value(book.Data.GetSheet(target).GetRow(reference.Row)?.GetCell(reference.Col));if(value!=null&&!string.IsNullOrWhiteSpace(Vb.Text(value)))values.Add(Vb.Text(value));}
        return values.Distinct().ToArray();
    }
    public object SaveParameters(string group,int row,InputChange[] changes)
        => SaveParameters(new[] { new ParameterChanges(group, row, changes) });

    public object BatchParameters()
    {
        using var b = Book();
        var data = ((ExcelBook)b.Book).Data;
        var groups = new[] { "equipment", "configuration", "motor", "pid", "materials" };
        ParameterForm BatchForm(string group, int row)
        {
            var form = Parameters(group, row, b);
            return group != "equipment" ? form : form with { Fields = form.Fields
                .Where(field => new CellReference(field.Address).Col is 3 or 5 or 6 or 7 or 8 or 14 or 27).ToList() };
        }
        return Enumerable.Range(0, 30).Where(row => HasEquipment(b, row)).Select(row => new
        {
            row,
            tag = b.Cell("input", "D" + (21 + row)),
            model = b.Cell("input", "N" + (21 + row)),
            review = ReviewStatus(data, 20 + row),
            proposalNotes = ProposalRow(data, 20 + row)?.GetCell(NotesColumn)?.ToString().Split('\n'),
            forms = groups.ToDictionary(group => group, group => BatchForm(group, row))
        }).ToArray();
    }

    public object SaveBatchParameters(ParameterChanges[] items)
    {
        if (items == null || items.Length > 180 || items.Any(item => item == null ||
            item.Group is not ("equipment" or "configuration" or "motor" or "pid" or "materials" or "review")))
            throw new ArgumentException("无效的批量提交参数。");
        return SaveParameters(items);
    }

    private object SaveParameters(ParameterChanges[] items)
    {
        using(var b=Book())
        {
          foreach (var (group, row, changes) in items.OrderBy(item => item.Group == "review"))
          {
            if (group == "review")
            {
                ConfirmDatasheetReview(b, row, changes);
                continue;
            }
            var allowed=Parameters(group,row,b).Fields.Where(f=>!f.ReadOnly).ToDictionary(f=>f.Sheet+"!"+f.Address);
            if(changes==null||changes.Any(c=>!allowed.ContainsKey(c.Sheet+"!"+c.Address)))throw new ArgumentException("参数不存在或为只读。");
            if (changes.Length > 0 && group != "project")
                InvalidateDatasheetReview(((ExcelBook)b.Book).Data, 20 + row);
            foreach(var c in changes)
            {
                if(c.Value?.TrimStart().StartsWith('=')==true)throw new ArgumentException("请输入参数值，不要输入公式。");
                var target=new CellReference(c.Address);var current=((ExcelBook)b.Book).Data.GetSheet(c.Sheet).GetRow(target.Row)?.GetCell(target.Col);
                if(current?.CellType==NPOI.SS.UserModel.CellType.Formula&&!(c.Sheet=="Sec-List"&&(target.Col==5||target.Col==13)))throw new InvalidOperationException("此单元格包含公式，请修改其输入参数："+c.Address);
                var targetRange = ((ExcelBook)b.Book).Worksheets[c.Sheet].Range[c.Address];
                if (group == "motor")
                {
                    targetRange.Value2 = MotorValue(allowed[c.Sheet+"!"+c.Address], c.Value);
                    continue;
                }
                if (group == "configuration" && target.Col == 4)
                {
                    var edition = ApiEdition.Number(c.Value);
                    if (edition == 0 && !string.IsNullOrWhiteSpace(c.Value))
                        throw new ArgumentException("请选择 API 610 第 7、8 或 10 版。");
                    targetRange.Value2 = edition == 0 ? null : edition + "版";
                }
                else if (c.Sheet == "input" && target.Row >= 20 && target.Col is 3 or 5 or 24 or 25 or 26 or 27)
                {
                    string value = c.Value?.Trim();
                    if (target.Col == 25 && string.Equals(value, "NO", StringComparison.OrdinalIgnoreCase))
                        value = "无";
                    targetRange.Value2 = string.IsNullOrEmpty(value) ? null : value;
                }
                else if (group == "hydraulics" && target.Col != 13)
                {
                    if (string.IsNullOrWhiteSpace(c.Value)) { targetRange.Value2 = null; continue; }
                    if (!double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric) ||
                        !double.IsFinite(numeric) || numeric < 0 || (target.Col != 15 && numeric == 0) ||
                        (target.Col == 16 && numeric > 1) || (target.Col == 17 && Math.Truncate(numeric) != numeric))
                        throw new ArgumentException("运行参数无效：" + c.Address);
                    targetRange.Value2 = numeric;
                }
                else if (group == "hydraulics")
                {
                    targetRange.Value2 = string.IsNullOrWhiteSpace(c.Value) ? null : NormalizeModel(c.Value);
                }
                else targetRange.Value2=current?.CellType!=NPOI.SS.UserModel.CellType.String&&double.TryParse(c.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out double number)?(object)number:c.Value;
            }
          }
            b.Save();
        }
        needsCalculation=true;output=null;return State();
    }
    internal static SheetOptions ReadOptions(WorkbookSession b)
    {
        var data=((ExcelBook)b.Book).Data;var sheet=data.GetSheet("CFL-App");string value=Convert.ToString(data.DocumentSummaryInformation?.CustomProperties?.Get("CFL_PrintSettings"));if(string.IsNullOrEmpty(value))value=sheet?.GetRow(0)?.GetCell(0)?.StringCellValue;
        return value==null?new():JsonSerializer.Deserialize<SheetOptions>(value)??new();
    }
    public object SaveOptions(SheetOptions options)
    {
        options.Validate();using(var b=Book()){var data=((ExcelBook)b.Book).Data;if(data.DocumentSummaryInformation==null)data.CreateInformationProperties();var properties=data.DocumentSummaryInformation.CustomProperties??new NPOI.HPSF.CustomProperties();properties.Put("CFL_PrintSettings",JsonSerializer.Serialize(options));data.DocumentSummaryInformation.CustomProperties=properties;b.Save();}return State();
    }
    public object Generate(string[] kinds,int[] rows)
    {
        if(needsCalculation)throw new InvalidOperationException("请先计算项目数据。");
        using(var b=Book())output=DrawingOutput.Generate(b,kinds,rows,ReadOptions(b),Report);return State();
    }
    public OutputEntry[] Files()=>output==null?Array.Empty<OutputEntry>():Directory.GetFiles(output,"*.dwg",SearchOption.AllDirectories).Select((p,i)=>new OutputEntry(i,Path.GetFileNameWithoutExtension(p),Path.GetFileName(Path.GetDirectoryName(p)))).ToArray();
    public string File(int id,string format)
    {
        if(format is not ("dwg" or "pdf" or "scene.json"))throw new ArgumentException("未知格式。");var files=output==null?Array.Empty<string>():Directory.GetFiles(output,"*.dwg",SearchOption.AllDirectories);
        if(id<0||id>=files.Length)throw new ArgumentOutOfRangeException(nameof(id));return Path.ChangeExtension(files[id],format);
    }
    public string Scene(int id)=>System.IO.File.ReadAllText(File(id,"scene.json"));
}
