using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PumpSelectionTools
{
    public sealed record PdfTrimCurves(string SourceFile, int Page, string CurveNumber, int Rpm, PdfImpeller[] Impellers)
    {
        public int Stages { get; init; } = 1;
        public bool UsesPlottedEfficiency { get; init; }
        public double[][] SharedNpshr { get; init; } = Array.Empty<double[]>();

        private static readonly Lazy<PdfTrimCurves> k50551 = new(() =>
        {
            using var data = typeof(PdfTrimCurves).Assembly.GetManifestResourceStream("PumpSelectionTools.Hydcalc.K50551-PDF21.json")
                ?? throw new FileNotFoundException("Missing K50551 PDF curve data.");
            return JsonSerializer.Deserialize<PdfTrimCurves>(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Invalid K50551 PDF curve data.");
        });

        public static PdfTrimCurves For(string curveNumber) => curveNumber == "K50551" ? k50551.Value : null;

        public double MinDiameter => Impellers.Min(impeller => impeller.DiameterMm);
        public double MaxDiameter => Impellers.Max(impeller => impeller.DiameterMm);

        private static PdfImpeller[] Bracket(IEnumerable<PdfImpeller> traces, double diameter)
        {
            var exact = traces.FirstOrDefault(candidate => Math.Abs(candidate.DiameterMm - diameter) < 1e-8);
            if (exact != null) return new[] { exact };
            var lower = traces.Where(candidate => candidate.DiameterMm <= diameter).MaxBy(candidate => candidate.DiameterMm);
            var upper = traces.Where(candidate => candidate.DiameterMm >= diameter).MinBy(candidate => candidate.DiameterMm);
            return lower == null || upper == null ? Array.Empty<PdfImpeller>() : new[] { lower, upper };
        }

        private PdfImpeller[] NpshrSources(double diameter) =>
            Bracket(Impellers.Where(candidate => candidate.Npshr.Length > 0), diameter);

        public bool IsPublishedDiameter(double diameter) =>
            Impellers.Any(impeller => Math.Abs(impeller.DiameterMm - diameter) < 1e-8);

        public double? DiameterAtDuty(int rpm, double flow, double head)
        {
            double speed = (double)rpm / Rpm;
            var traces = Impellers.OrderBy(impeller => impeller.DiameterMm).ToArray();
            double? previous = null;
            for (int i = 0; i < traces.Length; i++)
            {
                var current = AtFlow(traces[i].Head, flow / speed) * speed * speed;
                if (current is double exact && Math.Abs(exact - head) < 1e-8) return traces[i].DiameterMm;
                if (previous is double low && current is double high && low < head && head < high)
                    return traces[i - 1].DiameterMm + (traces[i].DiameterMm - traces[i - 1].DiameterMm) *
                        (head - low) / (high - low);
                previous = current;
            }
            return null;
        }

        public PdfTrimSample Sample(HydcalcCurve original, int rpm, double diameter, double flow) =>
            Sample(original, rpm, flow, Bracket(Impellers, diameter), NpshrSources(diameter), diameter);

        public PdfTrimSample[] Samples(HydcalcCurve original, int rpm, double diameter, double ratedFlow)
        {
            var hydraulics = Bracket(Impellers, diameter);
            var npsh = NpshrSources(diameter);
            double speed = (double)rpm / Rpm;
            var flows = hydraulics.SelectMany(trace => trace.Head.Concat(trace.Power)).Concat(npsh.SelectMany(trace => trace.Npshr))
                .Select(row => row[0] * speed);
            if (UsesPlottedEfficiency)
                flows = flows.Concat(Impellers.SelectMany(trace => trace.Power.Concat(trace.Efficiency))
                    .Concat(SharedNpshr).Select(row => row[0] * speed));
            if (npsh.Any(trace => trace.DiameterMm == original.MaxDiameterMm))
                flows = flows.Concat(original.Points.Select(point => point.FlowM3h * rpm / original.Rpm.Value));
            return flows.Append(ratedFlow).Distinct().Order()
                .Select(flow => Sample(original, rpm, flow, hydraulics, npsh, diameter)).ToArray();
        }

        private PdfTrimSample Sample(HydcalcCurve original, int rpm, double flow,
            PdfImpeller[] hydraulics, PdfImpeller[] npsh, double diameter)
        {
            double speed = (double)rpm / Rpm, speedSquared = speed * speed;
            double referenceFlow = flow / speed;
            double? Interpolate(PdfImpeller[] traces, Func<PdfImpeller, double?> value)
            {
                if (traces.Length == 0) return null;
                var lower = value(traces[0]);
                if (traces.Length == 1) return lower;
                return lower + (value(traces[1]) - lower) *
                    ((diameter - traces[0].DiameterMm) / (traces[1].DiameterMm - traces[0].DiameterMm));
            }
            var head = Interpolate(hydraulics, trace => AtFlow(trace.Head, referenceFlow)) * speedSquared;
            var powerSources = UsesPlottedEfficiency ? Bracket(Impellers.Where(trace => trace.Power.Length > 0), diameter) : hydraulics;
            var power = Interpolate(powerSources, trace => AtFlow(trace.Power, referenceFlow)) * speedSquared * speed;
            var efficiency = UsesPlottedEfficiency
                ? Interpolate(Bracket(Impellers.Where(trace => trace.Efficiency.Length > 0), diameter),
                    trace => AtFlow(trace.Efficiency, referenceFlow))
                : power is > 0 && head is > 0 && flow > 0 ? 100 * 9.81 * flow * head / (3600 * power) : null;
            // The BB2 source labels NPSHr at the sheet level, not at a particular trim.
            if (SharedNpshr.Length > 0)
            {
                var shared = AtFlow(SharedNpshr, referenceFlow) * speedSquared;
                return new PdfTrimSample(flow, head, power, efficiency, shared, shared == null ? null : "pdf-shared");
            }
            bool usedReference = false;
            var npshr = Interpolate(npsh, trace =>
            {
                if (AtFlow(trace.Npshr, referenceFlow) is double value) return value;
                if (trace.DiameterMm != original.MaxDiameterMm || original.Rpm is not > 0) return null;
                double inputSpeed = Rpm / original.Rpm.Value;
                if (!HydcalcMath.TryInterpolate(original.Points, referenceFlow / inputSpeed, p => p.Npshr, out value) ||
                    value <= 0) return null;
                usedReference = true;
                return value * inputSpeed * inputSpeed;
            });
            string source = npshr == null ? null : npsh.Length == 1 ? (usedReference ? "supplied-reference" : "pdf") :
                (usedReference ? "interpolated-with-reference" : "interpolated");
            return new PdfTrimSample(flow, head, power, efficiency, npshr * speedSquared, source);
        }

        // The traces are raster digitizations: interpolate between points without extending their domain.
        public static double? AtFlow(double[][] points, double flow)
        {
            if (points.Length == 0 || flow < points[0][0] - 1e-9 || flow > points[^1][0] + 1e-9) return null;
            flow = Math.Clamp(flow, points[0][0], points[^1][0]);
            int lo = 0, hi = points.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (flow < points[mid][0]) hi = mid;
                else lo = mid;
            }
            var a = points[lo];
            var b = points[hi];
            return hi == lo ? a[1] : a[1] + (b[1] - a[1]) * (flow - a[0]) / (b[0] - a[0]);
        }
    }

    public sealed record PdfImpeller(double DiameterMm, double[][] Head, double[][] Power, double[][] Npshr)
    {
        public double[][] Efficiency { get; init; } = Array.Empty<double[]>();
    }
    public readonly record struct PdfTrimSample(double Flow, double? Head, double? WaterPower,
        double? Efficiency, double? Npshr, string NpshrSource);
}
