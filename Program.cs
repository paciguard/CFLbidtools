using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 22 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.Services.AddSingleton<PumpWorkbench>();
builder.Services.AddSingleton<SubmissionWorkbench>();

var app = builder.Build();
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

app.MapGet("/api/pumps/options", (PumpWorkbench pumps) => pumps.Options());
app.MapGet("/api/pumps/catalog", (PumpWorkbench pumps, string? family, string? query, int? page) =>
    pumps.Catalog(family, query, Math.Clamp(page ?? 1, 1, 1000)));
app.MapPost("/api/pumps/search", (PumpWorkbench pumps, PumpRequest request) =>
    Execute(() => Results.Json(pumps.Search(request))));
app.MapPost("/api/pumps/curves", (PumpWorkbench pumps, CurveRequest request) =>
    Execute(() => Results.Json(pumps.Curves(request))));

app.MapPost("/api/submissions/command/{command}", async (string command, JsonElement payload, HttpContext context, SubmissionWorkbench submissions) =>
    await submissions.Dispatch(command, payload, context));
app.MapPost("/api/submissions/import", (HttpContext context, SubmissionWorkbench submissions) => submissions.Import(context));
app.MapGet("/api/submissions/file/{format}", (string format, int? id, HttpContext context, SubmissionWorkbench submissions) =>
    submissions.Download(format, id, context));

app.Run();

static IResult Execute(Func<IResult> operation)
{
    try { return operation(); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
}
