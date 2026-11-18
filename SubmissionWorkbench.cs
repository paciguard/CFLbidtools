using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ZTools.Offline;

public sealed class SubmissionWorkbench
{
    private const string Cookie = "CflSubmission";
    private readonly ConcurrentDictionary<string, Lazy<Session>> sessions = new();
    private readonly string storage;
    private readonly ILogger<SubmissionWorkbench> logger;
    private readonly PumpWorkbench pumps;

    public SubmissionWorkbench(IConfiguration settings, ILogger<SubmissionWorkbench> logger, PumpWorkbench pumps)
    {
        this.logger = logger;
        this.pumps = pumps;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        storage = settings["CFL_DATA_DIR"] ?? Path.Combine(root, "CFL", "WebWorkbench");
    }

    public Task<IResult> Dispatch(string command, JsonElement payload, HttpContext context) =>
        Run(context, session => Results.Json(command == "apply-pump"
            ? ApplyPump(session.Bridge.Project, payload.Deserialize<PumpTransfer>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            : session.Bridge.Dispatch(command, payload)), MobileBridge.IsImmediate(command));

    private object ApplyPump(OfflineProject project, PumpTransfer? transfer)
    {
        if (transfer?.Duty is null || transfer.Row is not int row || transfer.Index is not int index)
            throw new ArgumentException("\u8bf7\u5148\u9009\u62e9\u6cf5\u578b\u548c\u8bbe\u5907\u3002");
        var pump = pumps.GetSelection(transfer.Duty, index);
        return project.ApplyPumpSelection(row, new PumpSelectionValues(pump.Family, pump.Size, transfer.Duty.Flow,
            transfer.Duty.Head, pump.Npshr, pump.Efficiency, pump.Rpm));
    }

    private sealed record PumpTransfer(int? Row, int? Index, PumpRequest? Duty);

    public async Task<IResult> Import(HttpContext context)
    {
        if (!context.Request.HasFormContentType) return Error("\u8bf7\u9009\u62e9 Excel \u5de5\u4f5c\u7c3f\u3002");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var file = form.Files.GetFile("workbook");
        if (file is null || file.Length == 0 || file.Length > 20 * 1024 * 1024 ||
            Path.GetExtension(file.FileName).ToLowerInvariant() is not (".xlsx" or ".xls"))
            return Error("\u8bf7\u9009\u62e9 20 MB \u4ee5\u5185\u7684 .xlsx \u6216 .xls \u5de5\u4f5c\u7c3f\u3002");
        return await Run(context, session =>
        {
            using var input = file.OpenReadStream();
            return Results.Json(session.Bridge.Project.Import(input));
        });
    }

    public Task<IResult> Download(string format, int? id, HttpContext context) =>
        Run(context, session =>
        {
            var project = session.Bridge.Project;
            var path = format switch
            {
                "xlsx" => project.Workbook,
                "pdf" or "dwg" when id >= 0 => project.File(id.Value, format),
                _ => null
            };
            if (path is null || !File.Exists(path)) return Results.NotFound();
            var mime = format switch
            {
                "pdf" => "application/pdf",
                "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => "application/acad"
            };
            return Results.File(File.OpenRead(path), mime, Path.GetFileName(path));
        });

    private async Task<IResult> Run(HttpContext context, Func<Session, IResult> action, bool immediate = false)
    {
        var session = GetSession(context);
        if (!immediate) await session.Gate.WaitAsync(context.RequestAborted);
        try { return await Task.Run(() => action(session)); }
        catch (JsonException)
        {
            return Error("\u8bf7\u6c42\u6570\u636e\u4e0d\u6b63\u786e\u3002");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return Error(error.Message);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "SDOCtools request failed");
            return Error("\u65e0\u6cd5\u5904\u7406\u8bf7\u6c42\u3002\u8bf7\u68c0\u67e5\u5de5\u4f5c\u7c3f\u548c\u53c2\u6570\u3002");
        }
        finally { if (!immediate) session.Gate.Release(); }
    }

    private Session GetSession(HttpContext context)
    {
        var id = context.Request.Cookies[Cookie];
        if (id is not { Length: 48 } || !id.All(char.IsAsciiHexDigit) || !Directory.Exists(Path.Combine(storage, id)))
        {
            id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            context.Response.Cookies.Append(Cookie, id, new CookieOptions
            {
                MaxAge = TimeSpan.FromDays(180), HttpOnly = true, SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps, Path = "/", IsEssential = true
            });
        }
        return sessions.GetOrAdd(id, key => new Lazy<Session>(() => new Session(Path.Combine(storage, key)))).Value;
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private sealed class Session(string folder)
    {
        public MobileBridge Bridge { get; } = new(folder);
        public SemaphoreSlim Gate { get; } = new(1);
    }
}
