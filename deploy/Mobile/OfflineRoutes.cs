#if ANDROID || CFL_OFFLINE_TEST
using System.Text.Json;
using System.Web;
using ZTools.Offline;

internal sealed class OfflineRoutes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lazy<PumpWorkbench> pumps = new(() => new());
    private readonly Lazy<SubmissionSession> submissions;

    public OfflineRoutes(string folder) => submissions = new(() => new(folder, pumps.Value));

    public async Task<object> Dispatch(string path, JsonElement body, bool save)
    {
        if (path.Length > 2048 || !path.StartsWith("/api/", StringComparison.Ordinal))
            throw new ArgumentException("Unsupported offline request.");
        var url = new Uri(new Uri("https://appassets.androidplatform.net"), path);
        var route = url.AbsolutePath;
        var query = HttpUtility.ParseQueryString(url.Query);
        if (!save)
        {
            switch (route)
            {
                case "/api/version":
                    return WorkbenchVersion.Current;
                case "/api/pumps/options":
                    return pumps.Value.Options();
                case "/api/pumps/catalog":
                    return pumps.Value.Catalog(query["family"], query["query"],
                        int.TryParse(query["page"], out var page) ? Math.Clamp(page, 1, 1000) : 1);
                case "/api/pumps/search":
                    return pumps.Value.Search(Read<PumpRequest>(body));
                case "/api/pumps/import-datasheet":
                    return PumpDatasheet.Import(Read<PumpDatasheetUpload>(body));
                case "/api/pumps/trim-preview":
                    return pumps.Value.TrimPreview(Read<PumpRequest>(body));
                case "/api/pumps/curves":
                    return pumps.Value.Curves(Read<CurveRequest>(body));
            }
            const string commands = "/api/submissions/command/";
            if (route.StartsWith(commands, StringComparison.Ordinal))
            {
                var command = Uri.UnescapeDataString(route[commands.Length..]);
                return await WithSubmission(session => session.Dispatch(command, body), MobileBridge.IsImmediate(command));
            }
        }
        else
        {
            switch (route)
            {
                case "/api/pumps/export-pdf":
                    return PumpWorkbench.CreateCurvePdf(Read<CurvePdfRequest>(body));
                case "/api/pumps/export-trim-data":
                    return PumpWorkbench.ExportTrimData(Read<PumpRequest>(body));
                case "/api/pumps/export-batch":
                    return PumpWorkbench.ExportPdfBatch(Read<CurvePdfRequest[]>(body));
                case "/api/submissions/file/xlsx":
                case "/api/submissions/file/pdf":
                case "/api/submissions/file/dwg":
                    var file = await WithSubmission(session => session.Download(route.Split('/')[^1],
                        int.TryParse(query["id"], out var id) ? id : null));
                    return file ?? throw new ArgumentException("The file is not available.");
                case "/api/submissions/pdf-batch":
                    var bundle = await WithSubmission(session =>
                        session.DownloadPdfBatch(bool.TryParse(query["multiple"], out var multiple) && multiple));
                    return bundle ?? throw new ArgumentException("There are no generated drawings to export.");
            }
        }
        throw new ArgumentException("Unsupported offline request.");
    }

    private async Task<T> WithSubmission<T>(Func<SubmissionSession, T> action, bool immediate = false)
    {
        var session = submissions.Value;
        if (!immediate) await session.Gate.WaitAsync();
        try { return action(session); }
        finally { if (!immediate) session.Gate.Release(); }
    }

    private static T Read<T>(JsonElement body) =>
        body.Deserialize<T>(Json) ?? throw new ArgumentException("Invalid offline request.");
}
#endif
