using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using NPOI.HPSF;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ZTools.Engine;

// Only input is a public worksheet. Per-project values for the internal forms
// and calculated results travel with that XLSX as compressed document properties.
// Lookup tables and formulas are always reconstructed from the embedded model.
internal static class ProjectWorkbook
{
    static ProjectWorkbook()=>System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    static readonly string[] StateSheets={"Sheet1","PID","Sec-List","Utility","CFL-App"};
    const string Prefix="CFL_ProjectState_";
    sealed record CellValue(int Row,int Column,string Kind,string Value);
    sealed record SavedState(int Version,Dictionary<string,List<CellValue>> Sheets,string PrintSettings);

    internal static bool IsInputOnly(IWorkbook book)=>book.NumberOfSheets==1&&book.GetSheet("input")!=null;

    internal static HSSFWorkbook Materialize(IWorkbook project)
    {
        if(project.GetSheet("input")==null)throw new InvalidOperationException("项目文件必须包含 input 工作表。");
        using var template=TemplateResources.Open("ZTools.xls");
        var model=new HSSFWorkbook(template,true);
        try
        {
            var input=model.GetSheet("input");
            CopyInputLayout(project.GetSheet("input"),input);
            foreach(IRow row in input)foreach(var cell in row.Cells)cell.SetCellType(CellType.Blank);
            foreach(IRow row in project.GetSheet("input"))foreach(var cell in row.Cells)
                CopyCell(cell,(input.GetRow(row.RowNum)??input.CreateRow(row.RowNum)).GetCell(cell.ColumnIndex)??input.GetRow(row.RowNum).CreateCell(cell.ColumnIndex),true);
            var state=ReadState(project);
            if(state==null&&!IsInputOnly(project))state=Capture(project);
            if(state!=null)
            {
                foreach(var pair in state.Sheets)
                {
                    if(!StateSheets.Contains(pair.Key))throw new InvalidOperationException("项目参数包含无效的内部工作表。");
                    var sheet=model.GetSheet(pair.Key)??model.CreateSheet(pair.Key);
                    foreach(var value in pair.Value)
                    {
                        if(value.Row<0||value.Row>200||value.Column<0||value.Column>255)throw new InvalidOperationException("内部项目参数地址无效。");
                        var row=sheet.GetRow(value.Row)??sheet.CreateRow(value.Row);
                        Apply(row.GetCell(value.Column)??row.CreateCell(value.Column),value);
                    }
                }
                if(!string.IsNullOrWhiteSpace(state.PrintSettings)){var properties=Properties(model);properties.Put("CFL_PrintSettings",state.PrintSettings);model.DocumentSummaryInformation.CustomProperties=properties;}
            }
            return model;
        }
        catch{model.Close();throw;}
    }

    static void CopyCell(ICell source,ICell target,bool formulas)
    {
        if(source.CellType==target.CellType&&(source.CellType==CellType.Formula?formulas&&source.CellFormula==target.CellFormula:Value(source)==Value(target)))return;
        if(source.CellType==CellType.Formula&&formulas){target.SetCellFormula(source.CellFormula);return;}
        Apply(target,Value(source));
    }
    static CellValue Value(ICell cell)
    {
        var type=cell.CellType==CellType.Formula?cell.CachedFormulaResultType:cell.CellType;
        return type switch
        {
            CellType.Numeric=>new(cell.RowIndex,cell.ColumnIndex,"n",cell.NumericCellValue.ToString("R",CultureInfo.InvariantCulture)),
            CellType.Boolean=>new(cell.RowIndex,cell.ColumnIndex,"b",cell.BooleanCellValue?"1":"0"),
            CellType.String=>new(cell.RowIndex,cell.ColumnIndex,"s",cell.StringCellValue),
            CellType.Error=>new(cell.RowIndex,cell.ColumnIndex,"e",cell.ErrorCellValue.ToString(CultureInfo.InvariantCulture)),
            _=>new(cell.RowIndex,cell.ColumnIndex,"z",null)
        };
    }
    static void Apply(ICell cell,CellValue value)
    {
        cell.SetCellType(CellType.Blank);
        switch(value.Kind)
        {
            case "n":cell.SetCellValue(double.Parse(value.Value,CultureInfo.InvariantCulture));break;
            case "b":cell.SetCellValue(value.Value=="1");break;
            case "s":cell.SetCellValue(value.Value??"");break;
            case "e":cell.SetCellErrorValue(byte.Parse(value.Value,CultureInfo.InvariantCulture));break;
            case "z":break;
            default:throw new InvalidOperationException("项目参数值类型无效。");
        }
    }
    static SavedState Capture(IWorkbook book)
    {
        var sheets=new Dictionary<string,List<CellValue>>();
        foreach(string name in StateSheets)
        {
            var sheet=book.GetSheet(name);if(sheet==null)continue;
            sheets[name]=sheet.SelectMany(r=>r.Cells).Where(c=>c.CellType!=CellType.Formula).Select(Value).ToList();
        }
        string settings=Property(book,"CFL_PrintSettings");
        if(string.IsNullOrEmpty(settings))settings=book.GetSheet("CFL-App")?.GetRow(0)?.GetCell(0)?.ToString();
        return new(2,sheets,settings);
    }
    static CustomProperties Properties(HSSFWorkbook book)
    {
        if(book.DocumentSummaryInformation==null)book.CreateInformationProperties();
        var properties=book.DocumentSummaryInformation.CustomProperties??new CustomProperties();
        book.DocumentSummaryInformation.CustomProperties=properties;
        return properties;
    }
    static string Property(IWorkbook book,string name)=>book switch
    {
        HSSFWorkbook binary=>Convert.ToString(binary.DocumentSummaryInformation?.CustomProperties?.Get(name)),
        XSSFWorkbook xml=>Convert.ToString(xml.GetProperties().CustomProperties.GetProperty(name)?.Item),
        _=>throw new InvalidOperationException("不支持的 Excel 项目格式。")
    };
    static SavedState ReadState(IWorkbook book)
    {
        string countValue=Property(book,Prefix+"Count");
        if(string.IsNullOrEmpty(countValue))return null;
        if(!int.TryParse(countValue,out int count)||count<1||count>4096)throw new InvalidOperationException("项目内部参数记录损坏。");
        string text=string.Concat(Enumerable.Range(0,count).Select(i=>Property(book,Prefix+i.ToString("D4"))));
        try
        {
            using var data=new MemoryStream(Convert.FromBase64String(text));using var zip=new GZipStream(data,CompressionMode.Decompress);
            var state=JsonSerializer.Deserialize<SavedState>(zip);
            if(state?.Version!=2||state.Sheets==null)throw new InvalidOperationException("项目内部参数版本不受支持。");
            return state;
        }
        catch(Exception error)when(error is JsonException or FormatException or InvalidDataException){throw new InvalidOperationException("无法读取项目内部参数，请使用完整的项目文件。",error);}
    }
    internal static void WriteInput(HSSFWorkbook model,string path)
    {
        if(!Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("输入项目请保存为 .xlsx 格式。");
        using var template=TemplateResources.Open("Input.xlsx");
        using var input=new XSSFWorkbook(template);
        if(!IsInputOnly(input))throw new InvalidOperationException("内置 input 模板格式不正确。");
        var inputSheet=input.GetSheet("input");
        CopyInputLayout(model.GetSheet("input"),inputSheet);
        foreach(IRow row in inputSheet)foreach(var cell in row.Cells)
            if(model.GetSheet("input").GetRow(row.RowNum)?.GetCell(cell.ColumnIndex)==null)cell.SetCellType(CellType.Blank);
        foreach(IRow row in model.GetSheet("input"))foreach(var cell in row.Cells)
        {
            var targetRow=inputSheet.GetRow(row.RowNum)??inputSheet.CreateRow(row.RowNum);
            CopyCell(cell,targetRow.GetCell(cell.ColumnIndex)??targetRow.CreateCell(cell.ColumnIndex),cell.CellType!=CellType.Formula||!cell.CellFormula.Contains('!'));
        }
        var efficiencyStyles = new Dictionary<short, ICellStyle>();
        for (int index = 20; index < 50; index++)
        {
            var efficiency = inputSheet.GetRow(index)?.GetCell(16);
            if (efficiency?.CellType != CellType.Numeric || efficiency.NumericCellValue is <= 0 or > 1) continue;
            var format = efficiency.CellStyle;
            if (!efficiencyStyles.TryGetValue(format.Index, out var percentage))
            {
                percentage = input.CreateCellStyle();
                percentage.CloneStyleFrom(format);
                percentage.DataFormat = input.CreateDataFormat().GetFormat("0.00%");
                efficiencyStyles.Add(format.Index, percentage);
            }
            efficiency.CellStyle = percentage;
        }
        var state=Capture(model);
        // The supplied input sheet uses local dropdown ranges and TODAY only.
        // Preserve its layout and validations; never retain a broken cross-sheet formula.
        foreach(IRow row in input.GetSheet("input"))foreach(var cell in row.Cells)
            if(cell.CellType==CellType.Formula&&cell.CellFormula.Contains('!'))CopyCell(cell,cell,false);
        input.SetSheetHidden(0,false);input.SetActiveSheet(0);input.SetSelectedTab(0);input.FirstVisibleTab=0;
        var properties=input.GetProperties().CustomProperties;
        void Put(string name,string value)
        {
            var existing=properties.GetProperty(name);
            if(existing==null)properties.AddProperty(name,value);else existing.Item=value;
        }
        using var packed=new MemoryStream();
        using(var zip=new GZipStream(packed,CompressionLevel.Optimal,true))JsonSerializer.Serialize(zip,state);
        string text=Convert.ToBase64String(packed.ToArray());int count=(text.Length+239)/240;
        Put(Prefix+"Count",count.ToString(CultureInfo.InvariantCulture));
        for(int i=0;i<count;i++)Put(Prefix+i.ToString("D4"),text.Substring(i*240,Math.Min(240,text.Length-i*240)));
        string pending=path+".saving-"+Guid.NewGuid().ToString("N");
        try{using(var output=File.Create(pending))WriteXlsxPackage(input,output);if(File.Exists(path))File.Replace(pending,path,null);else File.Move(pending,path);}
        finally{if(File.Exists(pending))File.Delete(pending);}
    }
    static void WriteXlsxPackage(XSSFWorkbook input,Stream output)
    {
        // NPOI rewrites legacy drawing/control XML that Excel cannot reopen.
        // Keep those parts intact; publish only the edited input cells/layout,
        // their string/style tables, and application document properties.
        using var generated=new MemoryStream();input.Write(generated,true);generated.Position=0;
        using var updated=new ZipArchive(generated,ZipArchiveMode.Read);
        using var template=TemplateResources.Open("Input.xlsx");
        using var original=new ZipArchive(template,ZipArchiveMode.Read);
        using var package=new ZipArchive(output,ZipArchiveMode.Create,true);
        string ReadText(ZipArchiveEntry entry){using var reader=new StreamReader(entry.Open(),System.Text.Encoding.UTF8);return reader.ReadToEnd();}
        var changed=new HashSet<string>{"[Content_Types].xml","_rels/.rels","xl/sharedStrings.xml","xl/styles.xml","docProps/custom.xml"};
        foreach(string name in original.Entries.Select(e=>e.FullName).Concat(new[]{"docProps/custom.xml"}).Distinct())
        {
            using var target=package.CreateEntry(name,CompressionLevel.Optimal).Open();
            if(name=="xl/worksheets/sheet1.xml")
            {
                string text=ReadText(original.GetEntry(name)),edited=ReadText(updated.GetEntry(name));
                foreach(string tag in new[]{"sheetData","cols","sheetViews"})
                {
                    string pattern="<"+tag+@"\b[^>]*>.*?</"+tag+">";
                    var replacement=System.Text.RegularExpressions.Regex.Match(edited,pattern,System.Text.RegularExpressions.RegexOptions.Singleline);
                    if(!replacement.Success)throw new InvalidOperationException("无法保存 input 工作表布局。");
                    text=System.Text.RegularExpressions.Regex.Replace(text,pattern,_=>replacement.Value,System.Text.RegularExpressions.RegexOptions.Singleline);
                }
                using var writer=new StreamWriter(target,new System.Text.UTF8Encoding(false));writer.Write(text);
            }
            else
            {
                var entry=changed.Contains(name)?updated.GetEntry(name):original.GetEntry(name);
                using var source=entry.Open();source.CopyTo(target);
            }
        }
    }
    internal static void Create(string path, Action<HSSFWorkbook> initialize)
    {
        using var template = TemplateResources.Open("Input.xlsx");
        using var input = new XSSFWorkbook(template);
        using var model = Materialize(input);
        ClearInputs(model.GetSheet("input"), 21, 50, 4, 31);
        ClearInputs(model.GetSheet("PID"), 10, 39, 6, 9);
        ClearInputs(model.GetSheet("Sec-List"), 10, 39, 6, 15);
        ClearInputs(model.GetSheet("Utility"), 13, 42, 5, 11);
        foreach (var address in new[] { "F10", "F11", "F12", "F13", "F14", "F15", "K10", "L12", "L13", "L14" })
        {
            var cell = new NPOI.SS.Util.CellReference(address);
            ClearInputs(model.GetSheet("input"), cell.Row + 1, cell.Row + 1, cell.Col + 1, cell.Col + 1);
        }
        var project = model.GetSheet("Sheet1");
        ClearInputs(project, 4, 5, 3, 3);
        ClearInputs(project, 10, 10, 3, 3);
        project.GetRow(9).GetCell(1).SetCellValue("CFL订单号");
        initialize?.Invoke(model);
        WriteInput(model, path);
    }

    internal static void ClearInputs(ISheet sheet, int firstRow, int lastRow, int firstColumn, int lastColumn)
    {
        for (int row = firstRow; row <= lastRow; row++)
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                var cell = sheet.GetRow(row - 1)?.GetCell(column - 1);
                if (cell != null && cell.CellType != CellType.Formula) cell.SetCellType(CellType.Blank);
            }
    }
    internal static void CopyInputLayout(ISheet source,ISheet target)
    {
        target.DefaultRowHeight=source.DefaultRowHeight;target.DefaultColumnWidth=source.DefaultColumnWidth;
        target.DisplayGridlines=source.DisplayGridlines;
        foreach(IRow row in source)
        {
            var other=target.GetRow(row.RowNum)??target.CreateRow(row.RowNum);
            other.Height=row.Height;other.ZeroHeight=row.ZeroHeight;
        }
        for(int column=0;column<64;column++){target.SetColumnWidth(column,source.GetColumnWidth(column));target.SetColumnHidden(column,source.IsColumnHidden(column));}
        var pane=source.PaneInformation;
        if(pane?.IsFreezePane()==true)target.CreateFreezePane(pane.VerticalSplitPosition,pane.HorizontalSplitPosition,pane.VerticalSplitLeftColumn,pane.HorizontalSplitTopRow);
    }
    internal static string ImportLegacy(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var source=WorkbookFactory.Create(stream);
        if(source is XSSFWorkbook&&IsInputOnly(source)&&Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase))return path;
        using var model=Materialize(source);
        string folder=Path.Combine(Path.GetDirectoryName(path),"CFL-Input-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);string imported=Path.Combine(folder,"ZTools.xlsx");WriteInput(model,imported);return imported;
    }
}
