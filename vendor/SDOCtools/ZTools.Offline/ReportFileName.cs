#nullable enable
using System.Globalization;
using System.Text;

namespace ZTools.Offline;

public static class ReportFileName
{
    public static string Curve(string? tag, string? service, string pump, int rpm) =>
        Join("Curve", tag, service, pump, rpm.ToString(CultureInfo.InvariantCulture) + "RPM", "V0");

    internal static string Drawing(string kind, string tag, string service, string pump, string plan,
        string motor, string frame, string power, string revision)
    {
        string motorFrame = string.IsNullOrWhiteSpace(motor) || motor.Contains(frame, StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(motor) ? frame : motor)
            : motor + "-" + frame;
        var rating = string.IsNullOrWhiteSpace(power) ? "" : power.Trim() + "kW";
        var driver = string.Join("-", new[] { motorFrame, rating }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return kind + "__" + Join(tag, service, pump, string.Concat(plan.Where(c => !char.IsWhiteSpace(c))),
            driver, "V" + (string.IsNullOrWhiteSpace(revision) ? "0" : revision));
    }

    private static string Join(params string?[] parts) =>
        string.Join("_", parts.Select(SafePart).Where(part => part.Length != 0));

    private static string SafePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        const string invalid = "<>:\"/\\|?*";
        var result = new StringBuilder();
        int bytes = 0;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 48) break;
            result.Append(Rune.IsControl(rune) || (rune.IsAscii && invalid.Contains((char)rune.Value)) ? "-" : rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString().Trim().TrimEnd('.');
    }
}
