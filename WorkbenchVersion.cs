using System.Reflection;

internal sealed record WorkbenchVersion(string Version, string? BuildNumber, string Notes, string[] Revisions)
{
    internal static WorkbenchVersion Current { get; } = Load();

    private static WorkbenchVersion Load()
    {
        var assembly = typeof(WorkbenchVersion).Assembly;
        using var stream = assembly.GetManifestResourceStream("CflWorkbench.Revisions");
        using var revisions = stream == null ? null : new StreamReader(stream);
        using var notes = new StreamReader(assembly.GetManifestResourceStream("CflWorkbench.Changelog")
            ?? throw new InvalidOperationException("The bundled version notes are missing."));

        var number = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CflBuildNumber")?.Value;
        return new(assembly.GetName().Version!.ToString(3), number, notes.ReadToEnd(),
            revisions?.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);
    }
}
