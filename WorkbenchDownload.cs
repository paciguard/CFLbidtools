internal sealed record WorkbenchDownload(string Name, string Mime, Stream Content) : IDisposable
{
    public void Dispose() => Content.Dispose();
}
