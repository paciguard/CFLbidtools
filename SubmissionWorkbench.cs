using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ZTools.Offline;

public sealed class SubmissionWorkbench
{
    private const string Cookie = "CflSubmission";
    private readonly ConcurrentDictionary<string, Lazy<SubmissionSession>> sessions = new();
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
        Run(context, session => Results.Json(session.Dispatch(command, payload)), MobileBridge.IsImmediate(command));

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
            return Results.Json(session.Import(input));
        });
    }

    public Task<IResult> Download(string format, int? id, HttpContext context) =>
        Run(context, session => DownloadResult(session.Download(format, id)));

    public Task<IResult> DownloadPdfBatch(bool? multiple, HttpContext context) =>
        Run(context, session => DownloadResult(session.DownloadPdfBatch(multiple == true, context.RequestAborted)));

    private static IResult DownloadResult(WorkbenchDownload? file) =>
        file == null ? Results.NotFound() : Results.File(file.Content, file.Mime, file.Name);

    private async Task<IResult> Run(HttpContext context, Func<SubmissionSession, IResult> action, bool immediate = false)
    {
        var session = GetSession(context);
        if (!immediate) await session.Gate.WaitAsync(context.RequestAborted);
        try { return await Task.Run(() => action(session)); }
        catch (JsonException)
        {
            return Error("\u8bf7\u6c42\u6570\u636e\u4e0d\u6b63\u786e\u3002");
        }
        catch (SubmissionInputException error)
        {
            return Results.UnprocessableEntity(new { error = error.Message, issues = error.Issues });
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return Error(error.Message);
        }
        catch (Exception error)
        {
            var reference = context.TraceIdentifier;
            logger.LogWarning(error, "SDOCtools request failed ({Reference})", reference);
            string reason = error switch
            {
                IOException io when (io.HResult & 0xFFFF) is 32 or 33 =>
                    "项目文件正被其他程序使用。请关闭打开该文件的 Excel 或其他程序，再重试。",
                UnauthorizedAccessException => "应用无法读取或写入项目文件。请检查项目目录权限后重试。",
                IOException => "读取或保存项目文件失败。请检查文件是否可用及磁盘空间。",
                InvalidCastException or FormatException or OverflowException =>
                    "计算遇到无法识别的数据格式。请查看操作记录中的出错步骤。",
                _ => "处理项目时发生内部错误。请查看操作记录，或联系支持。"
            };
            return Results.Json(new { error = reason + " 错误编号：" + reference }, statusCode: 500);
        }
        finally { if (!immediate) session.Gate.Release(); }
    }

    private SubmissionSession GetSession(HttpContext context)
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
        return sessions.GetOrAdd(id, key => new Lazy<SubmissionSession>(() => new SubmissionSession(Path.Combine(storage, key), pumps))).Value;
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });
}
