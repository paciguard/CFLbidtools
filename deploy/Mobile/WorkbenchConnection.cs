using System.Net;
using System.Security.Authentication;

internal static class WorkbenchConnection
{
    internal const string SetupHint = "Paste the HTTPS link from CFL Workbench - Phone Connection on Windows, or use your company's workbench link.";
    internal const string Checking = "Checking connection...";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(12) };

    internal static string Parse(string? input, out Uri? address)
    {
        address = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0) return "Paste your workbench link first.";
        if (!Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : $"https://{text}", UriKind.Absolute, out var url) ||
            string.IsNullOrEmpty(url.Host) || !string.IsNullOrEmpty(url.UserInfo))
            return "This is not a valid workbench address.";
        if (url.IsLoopback || url.Host.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            url.Host.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(url.Host, out var ip) && ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())))
            return "Localhost points to this phone, not your PC. Use the HTTPS phone address from Windows.";
        if (url.Scheme != Uri.UriSchemeHttps)
            return "Use the secure HTTPS link. The HTTP link on your PC will not work here.";
        if (url.AbsolutePath != "/" || url.Query.Length != 0)
            return "Use the workbench's main address, without a page path or query.";
        address = url;
        return "";
    }

    internal static async Task<(bool Reachable, string Message)> CheckAsync(Uri address)
    {
        try
        {
            using var response = await Client.GetAsync(new Uri(address, "/api/connection"), HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.NoContent &&
                response.Headers.TryGetValues("X-CFL-Workbench", out var marker) && marker.Contains("1"))
                return (true, "");

            var code = (int)response.StatusCode;
            bool privateLink = address.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);
            if (privateLink && code is 401 or 403)
                return (false, "This phone is not allowed to use that workbench. Check its Tailscale sign-in and access permissions.");
            if (!privateLink && (code is 401 or 403 || (code is 301 or 302 or 303 or 307 or 308 &&
                response.Headers.Location is { } destination && (!destination.IsAbsoluteUri || destination.Scheme == Uri.UriSchemeHttps))))
                return (true, "Continue in your browser to sign in.");
            if (code is >= 500 and < 600)
                return (false, "The server is reachable, but the workbench is not responding. Check that it is open on the PC.");
            return (false, "This address did not respond as a CFL Workbench. Check the link and update the Windows workbench.");
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError ||
                                                error.InnerException is AuthenticationException)
        {
            return (false, "The HTTPS certificate could not be verified. Check the link or ask the workbench administrator.");
        }
        catch (HttpRequestException) { return Unreachable(); }
        catch (OperationCanceledException) { return Unreachable(); }
    }

    private static (bool, string) Unreachable() =>
        (false, "Unable to reach the workbench. Check the link, keep the PC workbench open, and check the phone's network connection.");
}
