using System;
using System.Collections.Generic;
using System.Linq;
using OxyPlot;
using OxyPlot.Series;

namespace PumpSelectionTools
{
    public static class PlotCurveFit
    {
        public static void Apply(PlotModel model, double? digitizedNpshDutyFlow = null)
        {
            foreach (var line in model.Series.OfType<LineSeries>())
            {
                if (line.Title == "MCSF") continue;
                Smooth(line.Points, digitizedNpshDutyFlow);
                if (line is AreaSeries area) Smooth(area.Points2, null);
            }
        }

        private static void Smooth(List<DataPoint> points, double? digitizedNpshDutyFlow)
        {
            var smoothed = new List<DataPoint>(points.Count);
            for (int start = 0; start < points.Count;)
            {
                if (!Valid(points[start]))
                {
                    smoothed.Add(points[start++]);
                    continue;
                }
                int end = start + 1;
                while (end < points.Count && Valid(points[end])) end++;
                var segment = points.GetRange(start, end - start);
                if (segment.Count >= 3 && segment.Zip(segment.Skip(1), (a, b) => b.X > a.X).All(increasing => increasing))
                {
                    if (digitizedNpshDutyFlow is double duty && segment.Count >= 7)
                        segment = FitDigitizedNpsh(segment, duty);
                    AddSpline(smoothed, segment);
                }
                else smoothed.AddRange(segment);
                start = end;
            }
            points.Clear();
            points.AddRange(smoothed);
        }

        private static List<DataPoint> FitDigitizedNpsh(List<DataPoint> raw, double duty)
        {
            const int intervals = 64;
            double first = raw[0].X, last = raw[^1].X, step = (last - first) / intervals;
            double minimum = raw.Min(point => point.Y), maximum = raw.Max(point => point.Y);
            double Filter(double q)
            {
                // Median removes isolated raster spikes; a short weighted fit softens the source handoff.
                double h = Math.Min(step, Math.Min(q - first, last - q) / 3);
                double Median(double x)
                {
                    double a = Sample(raw, x - 2 * h), b = Sample(raw, x), c = Sample(raw, x + 2 * h);
                    return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
                }
                return (Median(q - h) + 2 * Median(q) + Median(q + h)) / 4;
            }

            double radius = Math.Min(4 * step, Math.Min(duty - first, last - duty));
            double dutyY = radius > 0 ? Sample(raw, duty) : 0;
            double correction = radius > 0 ? dutyY - Filter(duty) : 0;
            var fitted = new List<DataPoint>(intervals + 2) { raw[0] };
            for (int i = 1; i < intervals; i++)
            {
                double q = first + i * step;
                if (radius > 0 && Math.Abs(q - duty) < step * 1e-6) continue;
                double weight = radius > 0 ? Math.Max(0, 1 - Math.Abs(q - duty) / radius) : 0;
                double y = Filter(q) + correction * weight * weight * (3 - 2 * weight);
                fitted.Add(new DataPoint(q, Math.Clamp(y, minimum, maximum)));
            }
            if (radius > 0) fitted.Add(new DataPoint(duty, dutyY));
            fitted.Add(raw[^1]);
            return fitted.OrderBy(point => point.X).ToList();
        }

        private static double Sample(List<DataPoint> points, double q)
        {
            int lo = 0, hi = points.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (q < points[mid].X) hi = mid;
                else lo = mid;
            }
            var a = points[lo];
            var b = points[hi];
            return a.Y + (b.Y - a.Y) * Math.Clamp((q - a.X) / (b.X - a.X), 0, 1);
        }

        private static void AddSpline(List<DataPoint> result, List<DataPoint> points)
        {
            var widths = new double[points.Count - 1];
            var slopes = new double[widths.Length];
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = points[i + 1].X - points[i].X;
                slopes[i] = (points[i + 1].Y - points[i].Y) / widths[i];
            }
            // Shape-preserving cubic Hermite slopes: plateaus and extrema do not introduce overshoot.
            var tangents = new double[points.Count];
            tangents[0] = slopes[0];
            tangents[^1] = slopes[^1];
            for (int i = 1; i < tangents.Length - 1; i++)
            {
                double left = slopes[i - 1], right = slopes[i];
                if (left == 0 || right == 0 || Math.Sign(left) != Math.Sign(right)) continue;
                double w1 = 2 * widths[i] + widths[i - 1], w2 = widths[i] + 2 * widths[i - 1];
                tangents[i] = (w1 + w2) / (w1 / left + w2 / right);
            }
            for (int i = 0; i < widths.Length; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                result.Add(a);
                for (int j = 1; j < 4; j++)
                {
                    double t = j / 4d, u = 1 - t;
                    double y = a.Y + t * t * (3 - 2 * t) * (b.Y - a.Y) +
                        t * u * widths[i] * (u * tangents[i] - t * tangents[i + 1]);
                    result.Add(new DataPoint(a.X + t * widths[i], Math.Clamp(y, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y))));
                }
            }
            result.Add(points[^1]);
        }

        private static bool Valid(DataPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    }
}
