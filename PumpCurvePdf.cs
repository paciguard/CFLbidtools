using System.Globalization;
using OxyPlot;
using OxyPlot.Axes;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZTools.Offline;
using ZTools.Standalone;

internal static class PumpCurvePdf
{
    private const double Margin = 22;
    private const double RowHeight = 7.6;

    internal static byte[] Create(PlotModel[] plots, PumpHit pump, PumpRequest duty, PumpReportMetrics report,
        PumpProjectDetails? details)
    {
        DrawingOutput.EnsurePdfFonts();
        using var pdf = new PdfDocument();
        pdf.Info.Title = "Pump Performance Curve";
        var page = pdf.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using var head = ExportPlot(plots[0], 365);
        using var npsh = ExportPlot(plots[1], 130);
        using var power = ExportPlot(plots[2], 130);
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            double width = page.Width.Point - 2 * Margin;
            double y = Margin;
            gfx.DrawRectangle(XBrushes.Black, Margin, y, width, 18);
            using var source = typeof(PumpCurvePdf).Assembly.GetManifestResourceStream("CflWorkbench.Brand.jpg")
                ?? throw new InvalidOperationException("The bundled CFL logo is missing.");
            using var logo = XImage.FromStream(source);
            gfx.DrawImage(logo, Margin + 2, y + 1.5, 15, 15);
            gfx.DrawString("凯乐泵业", new XFont("CFL", 8), XBrushes.White,
                new XRect(Margin + 20, y, 90, 18), XStringFormats.CenterLeft);
            gfx.DrawString("Pump Performance Curve / 泵性能曲线", new XFont("CFL", 10), XBrushes.White,
                new XRect(Margin, y, width, 18), XStringFormats.Center);
            y += 23;
            using var watermark = DrawingOutput.PdfWatermark();
            double logoSize = 415;
            gfx.DrawImage(watermark, Margin + (width-logoSize)/2, y + 21, logoSize, logoSize);
            foreach (var (plot, height) in new[] { (head, 270d), (npsh, 96d), (power, 96d) })
            {
                gfx.DrawImage(plot, Margin, y, width, height);
                gfx.DrawRectangle(new XPen(XColors.Black, .45), Margin, y, width, height);
                y += height + 4;
            }
            DrawTable(gfx, y, width, pump, duty, report, details);
        }
        using var output = new MemoryStream();
        pdf.Save(output, false);
        return output.ToArray();
    }

    private static XPdfForm ExportPlot(PlotModel plot, int height)
    {
        // Render at the report's logical plot size to preserve equal duty markers and aligned axes.
        if (height < 365)
            foreach (var axis in plot.Axes.Where(axis => axis.Position is AxisPosition.Left or AxisPosition.Right))
            {
                axis.FontSize = 10;
                axis.TitleFontSize = 10;
                axis.AxisTitleDistance += 6;
            }
        using var stream = new MemoryStream();
        // The pinned OxyPlot exporter keeps these charts vector without a native renderer.
#pragma warning disable CS0618
        PdfExporter.Export(plot, stream, 735, height);
#pragma warning restore CS0618
        return XPdfForm.FromStream(new MemoryStream(stream.ToArray(), writable: false));
    }

    private static string? F(double? value, string unit = "") =>
        value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + (unit == "" ? "" : " " + unit)
            : null;

    private static void DrawTable(XGraphics gfx, double y, double width, PumpHit p, PumpRequest d, PumpReportMetrics r,
        PumpProjectDetails? details)
    {
        double half = width / 2;
        var font = new XFont("CFL", 6.45);
        var line = new XPen(XColors.LightGray, .35);
        var ns = r.Ns is null && r.Nss is null ? null :
            (r.Ns?.ToString("0") ?? "-") + " / " + (r.Nss?.ToString("0") ?? "-") + " US";
        var factors = r.ViscosityFactors is { } values ? string.Join(" / ", values.Select(value => value.ToString("0.000"))) : null;
        var viscosity = d.ViscosityCp is null ? null : F(d.ViscosityCp, "cP") + " / " + F(r.ViscosityCst, "cSt");
        var left = new (string Label, string? Value)[]
        {
            ("Item number", details?.Tag), ("Service", details?.Service), ("Quantity", F(details?.Quantity)),
            ("Customer PO", details?.CustomerOrder), ("Project", details?.Project),
            ("Operating Conditions", null), ("Flow, rated", F(d.Flow, "m³/h")),
            ("Differential head, requested", F(d.Head, "m")), ("NPSH available", F(d.Npsha, "m")),
            ("Suction pressure, rated", null), ("Suction pressure, maximum", null),
            ("Site supply frequency", F(d.Hz, "Hz")), ("Performance", null),
            ("Speed criteria", d.Rpm != null ? "Fixed speed" : d.Poles != null ? d.Poles + " poles" : null),
            ("Speed, rated", F(p.Rpm, "rpm")), ("Hydraulic group", p.Group),
            ("Impeller diameter, rated", F(p.Diameter, "mm")), ("Impeller diameter, maximum", F(p.MaxDiameter, "mm")),
            ("Impeller diameter, minimum", F(p.MinDiameter, "mm")), ("Efficiency", F(p.Efficiency, "%")),
            (p.SourceRpm.HasValue || r.ViscosityCst is > 1 ? "NPSH required (est.)" : "NPSH required", F(p.Npshr, "m")),
            ("NPSH margin required", null), ("Impeller suction", p.Suction),
            ("Ns / Nss (rated BEP est.)", ns), ("MCSF (curve estimate)", F(r.McsfFlow, "m³/h")),
            ("Shutoff head (rated)", F(r.ShutoffHead, "m")),
            ("Head rise to shutoff", p.Head > 0 ? F((r.ShutoffHead / p.Head - 1) * 100, "%") : null),
            ("Flow, best efficiency point", F(r.BepFlow, "m³/h")),
            ("Flow ratio, rated / BEP", r.BepFlow > 0 ? F(d.Flow / r.BepFlow * 100, "%") : null),
            ("Diameter ratio, rated / max", F(p.Diameter / p.MaxDiameter * 100, "%")),
            ("Head ratio, rated / max", F(r.HeadRatio, "%")),
            (r.MixedStages && factors != null ? "Cq / Ch / Ce (main) / Cn (inlet)" : "Cq / Ch / Ce / Cn", factors),
            ("Selection status", p.SourceRpm.HasValue ? "Verify trim and NPSHr" :
                r.ViscosityCst is > 1 ? "HI estimate; verify NPSH" : null), ("Orientation", p.Orientation)
        };
        var right = new (string Label, string? Value)[]
        {
            ("Pump type / size", p.ModelSeries + " / " + p.Size), ("Stages", F(p.Stages)), ("Curve number", p.Curve),
            ("Date last saved", null), ("Client", details?.Client), ("Liquid", null),
            ("Liquid type", details?.Liquid), ("Additional liquid description", null), ("Solids diameter, maximum", null),
            ("Solids concentration, volume", null), ("Temperature, rated", F(d.Temperature, "°C")),
            ("Temperature, maximum", null), ("Fluid density, rated", F(d.Density, "kg/m³")),
            ("Fluid density, maximum", null), ("Viscosity, rated", viscosity), ("Vapor pressure, rated", null),
            ("Material", null), ("Material selected", details?.Material), ("Pressure Data", null), ("Outlet pressure", null),
            ("Maximum working pressure", null), ("Max. allowable working pressure", null),
            ("Max. allowable suction pressure", null), ("Hydrostatic test pressure", null), ("Driver & Power Data", null),
            ("Driver sizing specification", p.MotorKw != null ? "Rated shaft power" : null),
            ("Margin over specification", p.MotorKw != null ? F(r.MotorMargin, "%") : null), ("Service factor", null),
            ("Power, hydraulic", F(r.HydraulicPower, "kW")), ("Shaft power, rated", F(p.Power, "kW")),
            ("Power, max, rated diameter", F(r.PeakPower, "kW")), ("Minimum recommended motor", F(p.MotorKw, "kW")),
            ("Safety power", null)
        };
        gfx.DrawRectangle(XBrushes.LightGray, Margin, y, width, 5 * RowHeight);
        DrawColumn(left, Margin + 4);
        DrawColumn(right, Margin + half + 4);

        void DrawColumn((string Label, string? Value)[] rows, double x)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var (label, value) = rows[i];
                double top = y + i * RowHeight;
                gfx.DrawString(label, font, XBrushes.Black, new XRect(x, top, half, RowHeight), XStringFormats.CenterLeft);
                if (label is "Operating Conditions" or "Performance" or "Liquid" or "Material" or "Pressure Data" or "Driver & Power Data")
                {
                    gfx.DrawLine(line, x, top + RowHeight, x + half - 8, top + RowHeight);
                    continue;
                }
                double valueX = x + half * .61, available = half * .39 - 9;
                var text = string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
                if (gfx.MeasureString(text, font).Width > available)
                {
                    var clipped = new System.Text.StringBuilder();
                    foreach (var rune in text.EnumerateRunes())
                    {
                        if (gfx.MeasureString(clipped.ToString() + rune + "…", font).Width > available) break;
                        clipped.Append(rune.ToString());
                    }
                    text = clipped.ToString() + "…";
                }
                gfx.DrawString(":  " + text, font, XBrushes.Black, new XRect(valueX, top, available, RowHeight),
                    XStringFormats.CenterLeft);
            }
        }
    }
}
