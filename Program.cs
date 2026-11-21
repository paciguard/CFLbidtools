using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

bool openBrowser = args.Contains("--open-browser");
const string installedUrl = "http://127.0.0.1:5210/";
bool first = true;
using var installed = openBrowser ? new Mutex(true, @"Local\CFLbidtools-Installed", out first) : null;
if (openBrowser && !first)
{
    LaunchBrowser();
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(argument => argument != "--open-browser").ToArray(),
    ContentRootPath = openBrowser ? AppContext.BaseDirectory : null
});
if (openBrowser) builder.WebHost.UseUrls(installedUrl);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 22 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.Services.AddSingleton<PumpWorkbench>();
builder.Services.AddSingleton<SubmissionWorkbench>();

var app = builder.Build();
if (openBrowser)
{
    // The installed server only listens on loopback. Serve can forward HTTPS from there.
    app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });
    app.Use(async (context, next) =>
    {
        var request = context.Request;
        bool viaProxy = request.Headers.ContainsKey("X-Forwarded-Host") || request.IsHttps ||
            (request.Host.Host != "127.0.0.1" && !request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        if (viaProxy && (!request.IsHttps || request.Headers.ContainsKey("Tailscale-Funnel-Request") ||
            string.IsNullOrWhiteSpace(request.Headers["Tailscale-User-Login"])))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    });
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.Append("Referrer-Policy", "same-origin");
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers["X-CFL-Request"] != "1")
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
var assets = Path.Combine(AppContext.BaseDirectory, "SdocWeb");
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/sdoc-assets",
    FileProvider = new PhysicalFileProvider(assets)
});

app.MapGet("/api/connection", (HttpContext context) =>
{
    context.Response.Headers["X-CFL-Workbench"] = "1";
    if (openBrowser) context.Response.Headers["X-CFL-Installation"] = "windows";
    return Results.NoContent();
});
app.MapGet("/api/pumps/options", (PumpWorkbench pumps) => pumps.Options());
app.MapGet("/api/pumps/catalog", (PumpWorkbench pumps, string? family, string? query, int? page) =>
    pumps.Catalog(family, query, Math.Clamp(page ?? 1, 1, 1000)));
app.MapPost("/api/pumps/search", (PumpWorkbench pumps, PumpRequest request) =>
    Execute(() => Results.Json(pumps.Search(request))));
app.MapPost("/api/pumps/trim-preview", (PumpWorkbench pumps, PumpRequest request) =>
    Execute(() => Results.Json(pumps.TrimPreview(request))));
app.MapPost("/api/pumps/export-trim-data", (PumpRequest request) =>
    Execute(() => Download(PumpWorkbench.ExportTrimData(request))));
app.MapPost("/api/pumps/curves", (PumpWorkbench pumps, CurveRequest request) =>
    Execute(() => Results.Json(pumps.Curves(request))));
app.MapPost("/api/pumps/export-pdf", (CurvePdfRequest request) =>
    Execute(() => Download(PumpWorkbench.CreateCurvePdf(request))));
app.MapPost("/api/pumps/export-batch", (CurvePdfRequest[] requests, HttpContext context) =>
    Execute(() => Download(PumpWorkbench.ExportPdfBatch(requests, context.RequestAborted))));

app.MapPost("/api/submissions/command/{command}", async (string command, JsonElement payload, HttpContext context, SubmissionWorkbench submissions) =>
    await submissions.Dispatch(command, payload, context));
app.MapPost("/api/submissions/import", (HttpContext context, SubmissionWorkbench submissions) => submissions.Import(context));
app.MapGet("/api/submissions/file/{format}", (string format, int? id, HttpContext context, SubmissionWorkbench submissions) =>
    submissions.Download(format, id, context));
app.MapGet("/api/submissions/pdf-batch", (bool? multiple, HttpContext context, SubmissionWorkbench submissions) =>
    submissions.DownloadPdfBatch(multiple, context));

if (openBrowser) app.Lifetime.ApplicationStarted.Register(LaunchBrowser);
app.Run();

static void LaunchBrowser() => Process.Start(new ProcessStartInfo("http://localhost:5210/") { UseShellExecute = true });

static IResult Download(WorkbenchDownload file) => Results.File(file.Content, file.Mime, file.Name);

static IResult Execute(Func<IResult> operation)
{
    try { return operation(); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
}
