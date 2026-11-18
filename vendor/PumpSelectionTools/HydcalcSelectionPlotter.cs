using System;
using System.Linq;
using System.Collections.Generic;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Annotations;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace PumpSelectionTools
{
    public static class HydcalcSelectionPlotter
    {
        public static PlotModel[] Create(HydcalcSelection selection, double density, double? efficiencyOverride = null,
            double? dutyFlow = null, double? dutyHead = null, int? dutyRpm = null,
            double preferredMin = 0.7, double preferredMax = 1.2)
        {
            if (dutyFlow.HasValue && dutyHead.HasValue && dutyFlow > 0 && dutyHead > 0 &&
                (Math.Abs(dutyFlow.Value - selection.Flow) > 1e-8 || Math.Abs(dutyHead.Value - selection.Head) > 1e-6 ||
                 (dutyRpm.HasValue && dutyRpm.Value != selection.Rpm)))
            {
                int rpm = dutyRpm ?? selection.Rpm;
                double dutyMinimum = selection.Curve.MinDiameterMm.Value / selection.Curve.MaxDiameterMm.Value;
                if (selection.SuctionCurve != null) dutyMinimum = Math.Max(dutyMinimum,
                    selection.SuctionCurve.MinDiameterMm.Value / selection.SuctionCurve.MaxDiameterMm.Value);
                double ratio, actualHead, efficiency, npshr;
                if (!HydcalcSizing.TryDiameter(selection.Curve, selection.SuctionCurve, selection.Stages, rpm,
                    dutyFlow.Value, dutyHead.Value, dutyMinimum, out ratio) ||
                    !HydcalcSizing.TryEvaluate(selection.Curve, selection.SuctionCurve, selection.Stages, rpm,
                    ratio, dutyFlow.Value, selection.Options, out actualHead, out efficiency, out npshr))
                    throw new InvalidOperationException("The selected pump cannot produce the current duty. Run selection again.");
                selection = new HydcalcSelection { Property = selection.Property, Curve = selection.Curve,
                    SuctionCurve = selection.SuctionCurve, Stages = selection.Stages, Rpm = rpm, DiameterRatio = ratio,
                    Efficiency = efficiency, Npshr = npshr, Flow = dutyFlow.Value, Head = actualHead, Options = selection.Options };
            }
            var head = Model(selection.Property.Field(0) + " " + selection.Property.Field(1), "Head (m)");
            head.Legends.Add(new Legend { LegendPosition = LegendPosition.TopRight, LegendPlacement = LegendPlacement.Inside,
                LegendFontSize = 8, LegendSymbolLength = 18, LegendSymbolMargin = 4,
                LegendPadding = 5, LegendItemSpacing = 2, LegendBackground = OxyColors.White, LegendBorder = OxyColors.Gray });
            head.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Key = "Efficiency", Title = "Efficiency (%)",
                Minimum = 0, Maximum = 100, IsZoomEnabled = false, IsPanEnabled = false });
            head.Subtitle = selection.Stages + " stages" +
                (string.IsNullOrWhiteSpace(selection.HydraulicGroup) ? "" : " / Group " + selection.HydraulicGroup) +
                " / " + selection.Rpm + " rpm" +
                (selection.SuctionCurve == null ? " / " : "\n") + selection.HydraulicLabel;
            var npsh = Model("NPSHr", "NPSHr (m)");
            var power = Model("Shaft power", "Power (kW)");
            double qFactor, hFactor, eq, ef;
            HydcalcMath.TrimFactors(selection.Curve, selection.DiameterRatio, out qFactor, out hFactor, out eq, out ef);
            double maxFlow = selection.Curve.Points.Last().FlowM3h * selection.Rpm / selection.Curve.Rpm.Value;
            var flows = Enumerable.Range(0, 241).Select(i => maxFlow * i / 240).Concat(new[] { selection.Flow, maxFlow * qFactor })
                .GroupBy(q => Math.Round(q, 9)).Select(g => g.Any(q => q == selection.Flow) ? selection.Flow : g.First()).OrderBy(q => q).ToArray();
            var hRated = new LineSeries { Title = "Rated D=" + (selection.DiameterRatio * selection.Curve.MaxDiameterMm.Value).ToString("0.#") + " mm", Color = OxyColors.Blue, StrokeThickness = 3 };
            var hMax = new LineSeries { Title = "Maximum D=" + selection.Curve.MaxDiameterMm.Value.ToString("0.#") + " mm", Color = OxyColors.Black, LineStyle = LineStyle.Solid, StrokeThickness = 2 };
            var hMin = new LineSeries { Title = "Minimum diameter", Color = OxyColors.Gray, LineStyle = LineStyle.Solid, StrokeThickness = 2 };
            var nRated = new LineSeries { Title = "NPSHr", Color = OxyColors.Blue };
            var pRated = new LineSeries { Title = "Shaft power", Color = OxyColors.Blue };
            var efficiencyCurve = new LineSeries { Title = "Efficiency (%)", Color = OxyColors.Green,
                YAxisKey = "Efficiency", StrokeThickness = 2 };
            double minimum = selection.Curve.MinDiameterMm.Value / selection.Curve.MaxDiameterMm.Value;
            if (selection.SuctionCurve != null) minimum = Math.Max(minimum,
                selection.SuctionCurve.MinDiameterMm.Value / selection.SuctionCurve.MaxDiameterMm.Value);
            hMin.Title = "Minimum D=" + (minimum * selection.Curve.MaxDiameterMm.Value).ToString("0.#") + " mm";
            double bestEfficiency = double.NegativeInfinity, bestFlow = double.NaN, bestHead = double.NaN;
            foreach (double flow in flows)
            {
                double h, e, n;
                HydcalcSizing.TryEvaluate(selection.Curve, selection.SuctionCurve, selection.Stages, selection.Rpm,
                    selection.DiameterRatio, flow, selection.Options, out h, out e, out n);
                double ratedHead;
                bool hasRatedHead = HydcalcSizing.TryHead(selection.Curve, selection.SuctionCurve, selection.Stages, selection.Rpm,
                    selection.DiameterRatio, flow, out ratedHead);
                if (hasRatedHead)
                    hRated.Points.Add(new DataPoint(flow, ratedHead));
                else hRated.Points.Add(DataPoint.Undefined);
                if (!double.IsNaN(n) && n > 0) nRated.Points.Add(new DataPoint(flow, n));
                else nRated.Points.Add(DataPoint.Undefined);
                if (flow > 0 && hasRatedHead && e > 0 && e <= 100)
                {
                    if (e > bestEfficiency) { bestEfficiency = e; bestFlow = flow; bestHead = ratedHead; }
                    if (efficiencyOverride.HasValue) e = Math.Min(100, e * efficiencyOverride.Value / selection.Efficiency);
                    efficiencyCurve.Points.Add(new DataPoint(flow, e));
                    pRated.Points.Add(new DataPoint(flow, density * 9.81 * (flow / 3600) * ratedHead / (e / 100) / 1000));
                }
                else
                {
                    if (flow == 0 && selection.Curve.Points[0].FlowM3h == 0 && selection.Curve.Points[0].Efficiency == 0)
                        efficiencyCurve.Points.Add(new DataPoint(0, 0));
                    else efficiencyCurve.Points.Add(DataPoint.Undefined);
                    if (flow > 0) pRated.Points.Add(DataPoint.Undefined);
                }
                hMax.Points.Add(HydcalcSizing.TryHead(selection.Curve, selection.SuctionCurve, selection.Stages, selection.Rpm,
                    1, flow, out h) ? new DataPoint(flow, h) : DataPoint.Undefined);
                hMin.Points.Add(HydcalcSizing.TryHead(selection.Curve, selection.SuctionCurve, selection.Stages, selection.Rpm,
                    minimum, flow, out h) ? new DataPoint(flow, h) : DataPoint.Undefined);
            }
            var lowPower = new List<DataPoint>();
            foreach (var source in selection.Curve.Points.Where(p => p.FlowM3h > 0))
            {
                double q = source.FlowM3h * selection.Rpm / selection.Curve.Rpm.Value * qFactor;
                double h, e, n;
                HydcalcSizing.TryEvaluate(selection.Curve, selection.SuctionCurve, selection.Stages, selection.Rpm,
                    selection.DiameterRatio, q, selection.Options, out h, out e, out n);
                if (h <= 0 || double.IsNaN(h) || e <= 0 || e > 100 || double.IsNaN(e)) continue;
                if (efficiencyOverride.HasValue) e = Math.Min(100, e * efficiencyOverride.Value / selection.Efficiency);
                lowPower.Add(new DataPoint(q, density * 9.81 * (q / 3600) * h / (e / 100) / 1000));
                if (lowPower.Count == 4) break;
            }
            if (lowPower.Count >= 2 && selection.Curve.Points[0].FlowM3h <= 1e-8 &&
                (selection.SuctionCurve == null || selection.SuctionCurve.Points[0].FlowM3h <= 1e-8))
            {
                double shutoff = EstimateShutoffPower(lowPower);
                while (pRated.Points.Count > 0 && double.IsNaN(pRated.Points[0].X))
                    pRated.Points.RemoveAt(0);
                pRated.Points.Insert(0, new DataPoint(0, shutoff));
            }
            head.Series.Add(hMax); head.Series.Add(hMin); head.Series.Add(hRated); head.Series.Add(efficiencyCurve);
            npsh.Series.Add(nRated); power.Series.Add(pRated);
            AddConvertedAxis(npsh, 3.280839895, "NPSHr (ft)");
            AddConvertedAxis(power, 1.34102209, "Power (hp)");
            AddPreferredRegion(head, selection, minimum, preferredMin, preferredMax);
            double mcsfBase = double.NaN;
            bool isZ = string.Equals(selection.Property.Field(0).Trim(), "Z", StringComparison.OrdinalIgnoreCase);
            var npshCurve = selection.SuctionCurve ?? selection.Curve;
            if (isZ)
            {
                PumpTableCache.Load();
                var row = PumpTableCache.Rows.FirstOrDefault(r => string.Equals(r.PumpType.Trim(), "Z", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.PumpSize.Trim(), selection.Property.Field(1).Trim(), StringComparison.OrdinalIgnoreCase) && r.MinFlow > 0);
                if (row != null) mcsfBase = row.MinFlow * selection.Rpm / 1475.0;
            }
            else
            {
                var firstNpsh = npshCurve.Points.FirstOrDefault(p => p.Npshr > 0);
                if (firstNpsh != null) mcsfBase = firstNpsh.FlowM3h * selection.Rpm / npshCurve.Rpm.Value;
            }
            if (!double.IsNaN(mcsfBase))
                AddMcsfCurve(head, minimum, mcsfBase,
                    ratio => isZ || selection.Options.WithNpshCutting ? ratio : 1.0,
                    (ratio, q) => { double h; return HydcalcSizing.TryHead(selection.Curve, selection.SuctionCurve,
                        selection.Stages, selection.Rpm, ratio, q, out h) ? (double?)h : null; });
            LabelDiameter(head, hMax, "Max", selection.Curve.MaxDiameterMm.Value);
            LabelDiameter(head, hMin, "Min", minimum * selection.Curve.MaxDiameterMm.Value);
            LabelDiameter(head, hRated, "Rated", selection.DiameterRatio * selection.Curve.MaxDiameterMm.Value);
            if (!double.IsNaN(bestFlow)) AddBepMarker(head, bestFlow, bestHead);
            AddDutyMarker(head, selection.Flow, selection.Head);
            AddDutyMarker(npsh, selection.Flow, selection.Npshr);
            double eta = efficiencyOverride ?? selection.Efficiency;
            AddDutyMarker(power, selection.Flow, density * 9.81 * selection.Flow / 3600 * selection.Head / (eta / 100) / 1000);
            return new[] { head, npsh, power };
        }
        public static double EstimateShutoffPower(IList<DataPoint> points)
        {
            var first = points[0];
            var last = points[points.Count - 1];
            double estimate = first.Y - first.X * (last.Y - first.Y) / (last.X - first.X);
            return estimate > 0 && !double.IsInfinity(estimate) ? estimate : first.Y;
        }
        public static void AddConvertedAxis(PlotModel model, double factor, string title)
        {
            var metric = model.Axes.OfType<LinearAxis>().First(a => a.Position == AxisPosition.Left);
            double maximum = model.Series.OfType<LineSeries>().Where(s => !(s is AreaSeries))
                .SelectMany(s => s.Points).Where(p => !double.IsNaN(p.Y) && !double.IsInfinity(p.Y) && p.Y > 0)
                .Select(p => p.Y).DefaultIfEmpty(0).Max();
            maximum = maximum > 0 ? maximum * 1.1 : 1;
            metric.Minimum = 0; metric.Maximum = maximum;
            metric.IsZoomEnabled = false; metric.IsPanEnabled = false;
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Title = title, Minimum = 0,
                Maximum = maximum * factor, MajorGridlineStyle = LineStyle.None, MinorGridlineStyle = LineStyle.None,
                IsZoomEnabled = false, IsPanEnabled = false });
        }
        private static void AddPreferredRegion(PlotModel model, HydcalcSelection selection, double minRatio,
            double preferredMin, double preferredMax)
        {
            if (preferredMin < 0 || preferredMax <= preferredMin) return;
            var bep = HydcalcSizing.Bep(selection.Curve);
            double maxBepFlow = bep.FlowM3h * selection.Rpm / selection.Curve.Rpm.Value;
            if (maxBepFlow <= 0) return;
            double minFlowFactor, hFactor, effQ, effFactor;
            HydcalcMath.TrimFactors(selection.Curve, minRatio, out minFlowFactor, out hFactor, out effQ, out effFactor);
            var region = new AreaSeries { Title = "Preferred region", Fill = OxyColor.FromArgb(220, 0, 255, 255),
                Color = OxyColors.Cyan, StrokeThickness = 1 };
            bool started = false;
            for (int i = 0; i <= 80; i++)
            {
                double qMax = maxBepFlow * (preferredMin + (preferredMax - preferredMin) * i / 80.0);
                double qMin = qMax * minFlowFactor;
                double hMax, hMin = 0;
                bool valid = HydcalcSizing.TryHead(selection.Curve, selection.SuctionCurve, selection.Stages, selection.Rpm, 1, qMax, out hMax) &&
                    HydcalcSizing.TryHead(selection.Curve, selection.SuctionCurve, selection.Stages, selection.Rpm, minRatio, qMin, out hMin);
                if (!valid) { if (started) break; continue; }
                started = true;
                region.Points.Add(new DataPoint(qMax, hMax));
                region.Points2.Add(new DataPoint(qMin, hMin));
            }
            if (region.Points.Count >= 2)
            {
                if (Math.Abs(1 - minRatio) < 1e-9)
                {
                    var segment = new LineSeries { Title = "Preferred region", Color = OxyColors.Cyan, StrokeThickness = 8 };
                    foreach (var point in region.Points) segment.Points.Add(point);
                    model.Series.Insert(0, segment);
                }
                else model.Series.Insert(0, region);
            }
        }
        public static void AddMcsfCurve(PlotModel model, double minRatio, double baseFlow,
            Func<double, double> flowFactor, Func<double, double, double?> headAt)
        {
            if (baseFlow < 0 || minRatio <= 0 || minRatio > 1) return;
            var line = new LineSeries { Title = "MCSF", Color = OxyColors.Red, LineStyle = LineStyle.Dash, StrokeThickness = 2 };
            int valid = 0;
            for (int i = 0; i <= 40; i++)
            {
                double ratio = minRatio + (1 - minRatio) * i / 40.0;
                double q = baseFlow * flowFactor(ratio);
                double? h = headAt(ratio, q);
                if (h.HasValue && h.Value > 0 && !double.IsNaN(h.Value) && !double.IsInfinity(h.Value))
                { line.Points.Add(new DataPoint(q, h.Value)); valid++; }
                else line.Points.Add(DataPoint.Undefined);
            }
            if (valid >= 2) model.Series.Add(line);
        }
        private static PlotModel Model(string title, string axis)
        {
            var model = new PlotModel { Title = title, TitleFontSize = 12, SubtitleFontSize = 9 };
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Flow (m3/h)", Minimum = 0,
                MajorGridlineStyle = LineStyle.Solid, MinorGridlineStyle = LineStyle.Dot });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = axis, Minimum = 0,
                MajorGridlineStyle = LineStyle.Solid, MinorGridlineStyle = LineStyle.Dot });
            return model;
        }
        private static void LabelDiameter(PlotModel model, LineSeries series, string label, double diameter)
        {
            var points = series.Points.Where(p => !double.IsNaN(p.Y) && !double.IsInfinity(p.Y) && p.Y > 0).ToList();
            if (points.Count == 0) return;
            model.Annotations.Add(new PlotBoundaryTextAnnotation { Text = label + " D=" + diameter.ToString("0.#") + " mm",
                TextPosition = points[points.Count / 4], TextColor = series.Color, Stroke = OxyColors.Transparent,
                FontSize = 10, FontWeight = FontWeights.Bold });
        }
        public static void AddDutyMarker(PlotModel model, double x, double y)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) return;
            var points = model.Series.OfType<LineSeries>().Where(s => string.IsNullOrEmpty(s.YAxisKey))
                .SelectMany(s => s.Points).Where(p => !double.IsNaN(p.X) && !double.IsInfinity(p.X) &&
                    !double.IsNaN(p.Y) && !double.IsInfinity(p.Y)).ToList();
            if (points.Count == 0) return;
            double dx = Math.Max(points.Max(p => p.X) - points.Min(p => p.X), x) * 0.025;
            double dy = Math.Max(points.Max(p => p.Y) - points.Min(p => p.Y), y) * 0.04;
            var marker = new PolygonAnnotation { Fill = OxyColors.Blue, Stroke = OxyColors.Blue,
                StrokeThickness = 1, Tag = "RatedPointTriangle" };
            marker.Points.Add(new DataPoint(x, y));
            marker.Points.Add(new DataPoint(x, y - dy));
            marker.Points.Add(new DataPoint(x - dx, y));
            model.Annotations.Add(marker);
        }
        public static void AddBepMarker(PlotModel model, double x, double y)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) return;
            var marker = new ScatterSeries { Title = "BEP", MarkerType = MarkerType.Circle,
                MarkerFill = OxyColors.Red, MarkerSize = 5 };
            marker.Points.Add(new ScatterPoint(x, y));
            model.Series.Add(marker);
        }
    }

    public sealed class PlotBoundaryTextAnnotation : TextAnnotation
    {
        public OxyRect LastRenderedBounds { get; private set; }

        private struct ScreenSegment
        {
            public double X1, Y1, X2, Y2;
        }

        private static bool Intersects(OxyRect bounds, ScreenSegment line)
        {
            double t0 = 0, t1 = 1, dx = line.X2 - line.X1, dy = line.Y2 - line.Y1;
            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                double t = q / p;
                if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
                return true;
            }
            return Clip(-dx, line.X1 - bounds.Left) && Clip(dx, bounds.Right - line.X1) &&
                Clip(-dy, line.Y1 - bounds.Top) && Clip(dy, bounds.Bottom - line.Y1);
        }

        private List<ScreenSegment> CurveSegments()
        {
            var segments = new List<ScreenSegment>();
            foreach (var series in PlotModel.Series.OfType<LineSeries>())
            {
                if (series is AreaSeries || !series.IsVisible || series.XAxis == null || series.YAxis == null) continue;
                for (int i = 1; i < series.Points.Count; i++)
                {
                    var a = series.Points[i - 1]; var b = series.Points[i];
                    if (double.IsNaN(a.X) || double.IsNaN(a.Y) || double.IsNaN(b.X) || double.IsNaN(b.Y) ||
                        double.IsInfinity(a.X) || double.IsInfinity(a.Y) || double.IsInfinity(b.X) || double.IsInfinity(b.Y)) continue;
                    segments.Add(new ScreenSegment { X1 = series.XAxis.Transform(a.X), Y1 = series.YAxis.Transform(a.Y),
                        X2 = series.XAxis.Transform(b.X), Y2 = series.YAxis.Transform(b.Y) });
                }
            }
            return segments;
        }

        public override void Render(IRenderContext rc)
        {
            if (PlotModel == null || XAxis == null || YAxis == null || string.IsNullOrEmpty(Text))
            {
                base.Render(rc);
                return;
            }
            var area = PlotModel.PlotArea;
            if (area.Width <= 8 || area.Height <= 8) return;
            var originalPosition = TextPosition;
            var originalFontSize = FontSize;
            var originalHorizontal = TextHorizontalAlignment;
            var originalVertical = TextVerticalAlignment;
            try
            {
                TextHorizontalAlignment = HorizontalAlignment.Center;
                TextVerticalAlignment = VerticalAlignment.Middle;
                double width = 0, height = 0, selectedFontSize = ActualFontSize;
                double x = XAxis.Transform(originalPosition.X);
                double y = YAxis.Transform(originalPosition.Y);
                var occupied = new List<OxyRect>();
                var curves = CurveSegments();
                foreach (var note in PlotModel.Annotations.OfType<PlotBoundaryTextAnnotation>())
                {
                    if (note.XAxis == null || note.YAxis == null || string.IsNullOrEmpty(note.Text)) continue;
                    double baseFont = note.ActualFontSize, w = 0, h = 0, nx = 0, ny = 0, font = baseFont;
                    bool found = false;
                    foreach (double fraction in new[] { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5 })
                    {
                        font = baseFont * fraction;
                        var noteSize = rc.MeasureText(note.Text, note.ActualFont, font, note.ActualFontWeight);
                        w = noteSize.Width + note.Padding.Left + note.Padding.Right;
                        h = noteSize.Height + note.Padding.Top + note.Padding.Bottom;
                        if (w > area.Width - 8 || h > area.Height - 8) continue;
                        double originalX = note.XAxis.Transform(note.TextPosition.X), originalY = note.YAxis.Transform(note.TextPosition.Y);
                        nx = Math.Max(area.Left + 4 + w / 2, Math.Min(area.Right - 4 - w / 2, originalX));
                        ny = Math.Max(area.Top + 4 + h / 2, Math.Min(area.Bottom - 4 - h / 2, originalY));
                        var yCandidates = new List<double> { ny, area.Top + 4 + h / 2, area.Bottom - 4 - h / 2 };
                        for (double d = Math.Max(3, h / 3); d <= area.Height; d += Math.Max(3, h / 3))
                        { yCandidates.Add(ny - d); yCandidates.Add(ny + d); }
                        var xCandidates = new List<double> { nx, area.Left + 4 + w / 2, area.Right - 4 - w / 2 };
                        for (double d = Math.Max(8, w / 4); d <= area.Width; d += Math.Max(8, w / 4))
                        { xCandidates.Add(nx - d); xCandidates.Add(nx + d); }
                        foreach (double candidateX in xCandidates.OrderBy(v => Math.Abs(v - originalX)))
                        {
                            if (candidateX - w / 2 < area.Left + 4 || candidateX + w / 2 > area.Right - 4) continue;
                            foreach (double candidateY in yCandidates.OrderBy(v => Math.Abs(v - originalY)))
                            {
                                if (candidateY - h / 2 < area.Top + 4 || candidateY + h / 2 > area.Bottom - 4) continue;
                                var bounds = new OxyRect(candidateX - w / 2 - 3, candidateY - h / 2 - 3, w + 6, h + 6);
                                if (occupied.Any(other => bounds.Right > other.Left && bounds.Left < other.Right &&
                                    bounds.Bottom > other.Top && bounds.Top < other.Bottom)) continue;
                                if (curves.Any(line => Intersects(bounds, line))) continue;
                                nx = candidateX; ny = candidateY; found = true; break;
                            }
                            if (found) break;
                        }
                        if (found) break;
                    }
                    occupied.Add(new OxyRect(nx - w / 2, ny - h / 2, w, h));
                    if (ReferenceEquals(note, this)) { x = nx; y = ny; width = w; height = h; selectedFontSize = font; break; }
                }
                LastRenderedBounds = new OxyRect(x - width / 2, y - height / 2, width, height);
                FontSize = selectedFontSize;
                TextPosition = new DataPoint(XAxis.InverseTransform(x), YAxis.InverseTransform(y));
                base.Render(rc);
            }
            finally
            {
                TextPosition = originalPosition;
                FontSize = originalFontSize;
                TextHorizontalAlignment = originalHorizontal;
                TextVerticalAlignment = originalVertical;
            }
        }
    }
}
