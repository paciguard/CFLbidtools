using System.IO;

namespace PumpSelectionTools.Properties;

internal static class Resources
{
    public static byte[] HydraulicTable => Read("HydraulicTable.xlsx");
    public static byte[] SulzerCurveData => Read("SulzerCurveData.xlsx");

    private static byte[] Read(string name)
    {
        using var source = typeof(Resources).Assembly.GetManifestResourceStream("PumpSelectionTools.Workbooks." + name)
            ?? throw new FileNotFoundException(name);
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }
}
