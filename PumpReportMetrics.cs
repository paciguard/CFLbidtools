using OxyPlot;
using OxyPlot.Series;
using PumpSelectionTools;

internal sealed record PumpReportMetrics(double HydraulicPower, double MotorMargin, double? PeakPower,
    double? ShutoffHead, double? BepFlow, double? McsfFlow, double? HeadRatio, double? Ns, double? Nss,
    double? ViscosityCst, double[]? ViscosityFactors, bool MixedStages)
{
    internal static PumpReportMetrics Read(PlotModel[] plots, PumpHit pump, PumpRequest duty, HydcalcSelection selection,
        double motorMargin)
    {
        var head = plots[0].Series.OfType<LineSeries>();
        var rated = head.First(series => series.Title.StartsWith("Rated D=", StringComparison.Ordinal));
        var maximum = head.FirstOrDefault(series => series.Title.StartsWith("Maximum D=", StringComparison.Ordinal)) ?? rated;
        var mcsf = head.FirstOrDefault(series => series.Title == "MCSF");
        var bep = plots[0].Series.OfType<ScatterSeries>().FirstOrDefault(series => series.Title == "BEP")?
            .Points.FirstOrDefault();
        var npsh = plots[1].Series.OfType<LineSeries>().First(series => series.Title == "NPSHr");
        var power = plots[2].Series.OfType<LineSeries>().First(series => series.Title == "Shaft power");
        var maxHead = Interpolate(maximum.Points, duty.Flow);
        double? ns = null, nss = null;
        var eyes = pump.Suction.Equals("double", StringComparison.OrdinalIgnoreCase) ? 2 :
            pump.Suction.Equals("single", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (bep is { X: > 0, Y: > 0 } && eyes != 0)
        {
            // US specific speeds use flow per suction eye and head per stage.
            var speedFlow = pump.Rpm * Math.Sqrt(bep.X * 4.4028675393 / eyes);
            if (selection.SuctionCurve == null) ns = speedFlow / Math.Pow(bep.Y / pump.Stages * 3.280839895, .75);
            if (Interpolate(npsh.Points, bep.X) is > 0 and var npshAtBep)
                nss = speedFlow / Math.Pow(npshAtBep * 3.280839895, .75);
        }
        var factors = duty.ViscosityCp.HasValue ? selection.ViscosityFactors() : null;
        return new(duty.Density * 9.81 * duty.Flow * pump.Head / 3600000, motorMargin,
            power.Points.Where(point => Valid(point) && point.Y > 0).Select(point => (double?)point.Y).Max(),
            Interpolate(rated.Points, 0), bep?.X,
            selection.PdfReference?.MinimumFlowM3h ?? (mcsf is null ? null : Intersect(rated.Points, mcsf.Points)),
            maxHead is > 0 ? 100 * pump.Head / maxHead : null, ns, nss,
            duty.ViscosityCp.HasValue ? selection.ViscosityCst : null,
            factors is { } f ? [f.Flow, f.Head, f.Efficiency, f.Npsh] : null, selection.SuctionCurve != null);
    }

    private static bool Valid(DataPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    internal static double? Interpolate(IList<DataPoint> points, double x)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var b = points[i];
            if (!Valid(b)) continue;
            if (Math.Abs(x - b.X) < 1e-9) return b.Y;
            if (i == 0) continue;
            var a = points[i - 1];
            if (Valid(a) && a.X < x && x < b.X)
                return a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X);
        }
        return null;
    }

    private static double? Intersect(IList<DataPoint> curve, IList<DataPoint> boundary)
    {
        for (var i = 1; i < curve.Count; i++)
        {
            var a = curve[i - 1];
            var b = curve[i];
            if (!Valid(a) || !Valid(b)) continue;
            for (var j = 1; j < boundary.Count; j++)
            {
                var c = boundary[j - 1];
                var d = boundary[j];
                if (!Valid(c) || !Valid(d)) continue;
                var rx = b.X - a.X;
                var ry = b.Y - a.Y;
                var sx = d.X - c.X;
                var sy = d.Y - c.Y;
                var cross = rx * sy - ry * sx;
                if (Math.Abs(cross) < 1e-12) continue;
                var dx = c.X - a.X;
                var dy = c.Y - a.Y;
                var t = (dx * sy - dy * sx) / cross;
                var u = (dx * ry - dy * rx) / cross;
                if (t >= -1e-9 && t <= 1 + 1e-9 && u >= -1e-9 && u <= 1 + 1e-9)
                    return a.X + t * rx;
            }
        }
        return null;
    }
}
