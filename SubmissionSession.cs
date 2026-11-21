using System.IO.Compression;
using System.Text.Json;
using ZTools.Offline;

internal sealed class SubmissionSession(string folder, PumpWorkbench pumps)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly MobileBridge bridge = new(folder);
    public SemaphoreSlim Gate { get; } = new(1);

    public object Dispatch(string command, JsonElement payload) => command switch
    {
        "apply-pump" => ApplyPump(bridge.Project, ReadSelection(payload)),
        "pump-models" => OfflineProject.PumpModels(ResolvePump(ReadSelection(payload))),
        "new-from-pump" => CreateFromPump(bridge.Project, ReadSelection(payload)),
        "batch-models" => ReadBatch(payload).Select(item => OfflineProject.PumpModels(ResolvePump(item))).ToArray(),
        "new-from-batch" => bridge.Project.CreateBatch(ReadBatch(payload).Select(item =>
            new ProjectPump(ResolvePump(item), item.Model!, item.Details!, item.Duty?.Temperature)).ToArray()),
        _ => bridge.Dispatch(command, payload)
    };

    private static PumpTransfer? ReadSelection(JsonElement payload) =>
        payload.Deserialize<PumpTransfer>(Json);

    private static PumpTransfer[] ReadBatch(JsonElement payload)
    {
        var items = payload.GetProperty("items").Deserialize<PumpTransfer[]>(Json);
        if (items is not { Length: >= 1 and <= 30 } || items.Any(item => item is null))
            throw new ArgumentException("批量项目须包含 1 至 30 台设备。");
        return items;
    }

    private PumpSelectionValues ResolvePump(PumpTransfer? transfer)
    {
        if (transfer?.Duty is null || transfer.Index is not int index)
            throw new ArgumentException("请先选择泵型。");
        var pump = pumps.GetSelection(transfer.Duty, index);
        return new PumpSelectionValues(pump.Family, pump.Size, transfer.Duty.Flow,
            transfer.Duty.Head, pump.Npshr, pump.Efficiency, pump.Rpm, transfer.Duty.Hz, pump.MotorKw);
    }

    private object ApplyPump(OfflineProject project, PumpTransfer? transfer)
    {
        if (transfer?.Row is not int row) throw new ArgumentException("请先选择设备。");
        return project.ApplyPumpSelection(row, ResolvePump(transfer));
    }

    private object CreateFromPump(OfflineProject project, PumpTransfer? transfer) =>
        project.Create(ResolvePump(transfer), transfer?.Model, transfer?.Details,
            transfer?.Duty?.Temperature);

    private sealed record PumpTransfer(int? Row, int? Index, PumpRequest? Duty, string? Model,
        PumpProjectDetails? Details);


    public object Import(Stream input) => bridge.Project.Import(input);

    public WorkbenchDownload? Download(string format, int? id)
    {
        var project = bridge.Project;
        var path = format switch
        {
            "xlsx" => project.Workbook,
            "pdf" or "dwg" when id >= 0 => project.File(id.Value, format),
            _ => null
        };
        if (path is null || !File.Exists(path)) return null;
        var mime = format switch
        {
            "pdf" => "application/pdf",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/acad"
        };
        return new(Path.GetFileName(path), mime, File.OpenRead(path));
    }

    public WorkbenchDownload? DownloadPdfBatch(bool multiple, CancellationToken cancellation = default)
    {
        var project = bridge.Project;
        var files = project.Files();
        if (files.Length == 0) return null;
        bool split = multiple || ((OfflineState)project.State()).Rows.Length > 1;
        var package = new MemoryStream();
        try
        {
            using var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var path = project.File(file.Id, "pdf");
                string name = (split ? file.Kind + "/" : "") + Path.GetFileName(path);
                using var input = File.OpenRead(path);
                using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                input.CopyTo(entry);
            }
        }
        catch { package.Dispose(); throw; }
        package.Position = 0;
        return new("Drawings_V0.zip", "application/zip", package);
    }
}
