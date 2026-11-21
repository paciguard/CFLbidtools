#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Java.Interop;
using System.Text.Json;
using ZTools.Offline;

[Activity(Label = "CFL Engineering Workbench", MainLauncher = true, Exported = true,
    WindowSoftInputMode = SoftInput.StateAlwaysHidden,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed class OfflineActivity : Activity
{
    private const int SaveRequest = 13;
    private WebView? view;
    private OfflineInterface? bridge;
    private TaskCompletionSource<Android.Net.Uri?>? pendingSave;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var directory = Path.Combine(FilesDir!.AbsolutePath, "workbench-projects");
        bridge = new OfflineInterface(this, new OfflineRoutes(directory));
        view = new WebView(this);
        view.Settings.JavaScriptEnabled = true;
        view.Settings.DomStorageEnabled = true;
        view.Settings.AllowFileAccess = false;
        view.Settings.AllowContentAccess = false;
        view.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        view.Settings.BlockNetworkLoads = true;
        view.SetWebViewClient(new OfflineAssets(Assets!));
        view.AddJavascriptInterface(bridge, "CflDevice");
        SetContentView(view);
        view.LoadUrl(OfflineAssets.Origin + "/");
    }

    public override bool OnCreateOptionsMenu(IMenu? menu)
    {
        menu?.Add(0, 1, 0, "Connect to PC");
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        if (item.ItemId != 1) return base.OnOptionsItemSelected(item);
        StartActivity(new Intent(this, typeof(OnlineActivity)));
        return true;
    }

    internal void Deliver(int id, string response) => RunOnUiThread(() =>
    {
        if (view == null) return;
        foreach (var script in MobileBridge.Scripts(id, response))
            view.EvaluateJavascript(script, null);
    });

    internal async Task<bool> Save(WorkbenchDownload document)
    {
        var request = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (IsDestroyed || IsFinishing) { request.TrySetResult(null); return; }
            if (pendingSave != null) { request.TrySetException(new InvalidOperationException("Another save is open.")); return; }
            pendingSave = request;
            try
            {
                using var intent = new Intent(Intent.ActionCreateDocument);
                intent.AddCategory(Intent.CategoryOpenable);
                intent.SetType(document.Mime);
                intent.PutExtra(Intent.ExtraTitle, document.Name);
                StartActivityForResult(intent, SaveRequest);
            }
            catch (Exception error)
            {
                pendingSave = null;
                request.TrySetException(error);
            }
        });
        var uri = await request.Task.ConfigureAwait(false);
        if (uri == null) return false;
        using var output = ContentResolver!.OpenOutputStream(uri, "w")
            ?? throw new IOException("Unable to write the selected file.");
        await document.Content.CopyToAsync(output).ConfigureAwait(false);
        return true;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != SaveRequest) return;
        var current = pendingSave;
        pendingSave = null;
        current?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }

    protected override void OnDestroy()
    {
        pendingSave?.TrySetResult(null);
        pendingSave = null;
        view?.RemoveJavascriptInterface("CflDevice");
        if (view?.Parent is ViewGroup parent) parent.RemoveView(view);
        view?.Destroy();
        view = null;
        base.OnDestroy();
    }
}

internal sealed class OfflineInterface(OfflineActivity activity, OfflineRoutes routes) : Java.Lang.Object
{
    [JavascriptInterface, Export("request")]
    public void Request(string text)
    {
        if (text == null || text.Length > 512 * 1024) return;
        _ = Task.Run(async () =>
        {
            var id = -1;
            try
            {
                using var json = JsonDocument.Parse(text);
                var value = json.RootElement;
                id = value.GetProperty("id").GetInt32();
                var path = value.GetProperty("path").GetString() ?? "";
                var save = value.GetProperty("save").GetBoolean();
                var result = await routes.Dispatch(path, value.GetProperty("data"), save);
                if (result is WorkbenchDownload document)
                {
                    using (document) result = new { saved = await activity.Save(document) };
                }
                activity.Deliver(id, MobileBridge.Reply(id, result));
            }
            catch (Exception error)
            {
                Android.Util.Log.Warn("CFL.Workbench", error.ToString());
                if (id >= 0) activity.Deliver(id, MobileBridge.Failure(id, error));
            }
        });
    }
}

internal sealed class OfflineAssets(Android.Content.Res.AssetManager assets) : WebViewClient
{
    internal const string Origin = "https://appassets.androidplatform.net";
    private const string Policy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; frame-src 'self'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

    private static bool IsLocal(Android.Net.Uri? url) =>
        url?.Scheme == "https" && url.Host == "appassets.androidplatform.net" && url.Port is -1 or 443;

    public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => !IsLocal(request?.Url);

    public override WebResourceResponse ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
    {
        var url = request?.Url;
        if (!IsLocal(url) || request?.Method != "GET") return NotFound();
        var path = url?.Path;
        if (path == "/") path = "/index.html";
        if (path == null || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\')) return NotFound();
        var mime = Path.GetExtension(path) switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" or ".webmanifest" => "application/json",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".ico" => "image/x-icon",
            _ => null
        };
        if (mime == null) return NotFound();
        try
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Security-Policy"] = Policy,
                ["X-Content-Type-Options"] = "nosniff"
            };
            return new WebResourceResponse(mime, mime.StartsWith("image/", StringComparison.Ordinal) ? null : "utf-8",
                200, "OK", headers, assets.Open("site" + path));
        }
        catch (Java.IO.IOException) { return NotFound(); }
        catch (IOException) { return NotFound(); }
    }

    private static WebResourceResponse NotFound() =>
        new("text/plain", "utf-8", 404, "Not Found", new Dictionary<string, string>(), new MemoryStream());
}
#endif
