using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PumpSelectionTools
{
    public sealed class HydcalcDuty
    {
        public double Flow, Head, Density = 1000;
        public double? Npsha, TemperatureC;
        public int Hz = 50;
        public int? Poles;
        public int? FixedRpm;
    }
    public sealed class HydcalcSizingOptions
    {
        // hydcalc/hydcalc.ini automatic-selection defaults.
        public double MinBepRatio = 0.4, MaxBepRatio = 1.3, NpshMargin = 1.15;
        public bool WithNpshCutting = false;
    }
    public sealed class HydcalcSelection
    {
        public HydcalcRecord Property { get; internal set; }
        public HydcalcCurve Curve { get; internal set; }
        public HydcalcCurve SuctionCurve { get; internal set; }
        public int Stages { get; internal set; }
        public int Rpm { get; internal set; }
        public double DiameterRatio { get; internal set; }
        public double Efficiency { get; internal set; }
        public double Npshr { get; internal set; }
        public double Flow { get; internal set; }
        public double Head { get; internal set; }
        public double PowerKw { get; internal set; }
        public HydcalcSizingOptions Options { get; internal set; }
        public string HydraulicLabel { get { return Curve.Name + " #" + Curve.Id +
            (SuctionCurve == null ? "" : " + suction " + SuctionCurve.Name + " #" + SuctionCurve.Id); } }
        public string HydraulicGroup
        {
            get
            {
                if (Stages < 2 || Curve == null || string.IsNullOrWhiteSpace(Curve.Variant)) return "";
                string group = Curve.Variant.Trim();
                if (SuctionCurve == null) return group;
                if (string.IsNullOrWhiteSpace(SuctionCurve.Variant)) return "";
                return group + " / " + SuctionCurve.Variant.Trim() + " (suction)";
            }
        }
        public string FlowType
        {
            get
            {
                if (string.IsNullOrEmpty(HydraulicGroup)) return "";
                string main = (SuctionCurve == null ? Stages : Stages - 1).ToString(CultureInfo.InvariantCulture)
                    + Curve.Variant.Trim();
                return SuctionCurve == null ? main : main + " + 1" + SuctionCurve.Variant.Trim() + " (suction)";
            }
        }
    }

    public static class HydcalcSizing
    {
        public static readonly Dictionary<Tuple<int, int>, int> Speeds = new Dictionary<Tuple<int, int>, int>
        {
            {Tuple.Create(50,2),2950}, {Tuple.Create(50,4),1480}, {Tuple.Create(50,6),980},
            {Tuple.Create(50,8),735}, {Tuple.Create(50,10),590}, {Tuple.Create(50,12),490},
            {Tuple.Create(60,2),3550}, {Tuple.Create(60,4),1780}, {Tuple.Create(60,6),1180},
            {Tuple.Create(60,8),885}, {Tuple.Create(60,10),710}, {Tuple.Create(60,12),590}
        };

        public static List<HydcalcSelection> Search(HydcalcCatalog catalog, HydcalcDuty duty, HydcalcSizingOptions options = null)
        {
            options = options ?? new HydcalcSizingOptions();
            if (!Finite(duty.Flow) || duty.Flow <= 0 || !Finite(duty.Head) || duty.Head <= 0 ||
                !Finite(duty.Density) || duty.Density <= 0 || (duty.Hz != 50 && duty.Hz != 60) ||
                (duty.FixedRpm.HasValue && duty.FixedRpm.Value <= 0) ||
                (duty.Npsha.HasValue && (!Finite(duty.Npsha.Value) || duty.Npsha.Value < 0)) ||
                (duty.TemperatureC.HasValue && !Finite(duty.TemperatureC.Value)))
                throw new ArgumentException("Invalid operating conditions.");
            if (options.MinBepRatio < 0 || options.MaxBepRatio <= options.MinBepRatio || options.NpshMargin < 1)
                throw new ArgumentException("Invalid catalog selection limits.");

            var eligible = new Dictionary<string, HydcalcCurve>();
            foreach (var group in catalog.Curves.GroupBy(c => c.Name))
            {
                var curves = group.ToList();
                bool exactCzDuplicates = curves.Count > 1 && curves.All(c =>
                    string.Equals(c.Family, "CZ", StringComparison.OrdinalIgnoreCase) && SameCurve(curves[0], c));
                if ((curves.Count == 1 && Eligible(curves[0])) ||
                    (exactCzDuplicates && EligibleIgnoringDuplicates(curves[0])))
                    eligible.Add(group.Key, curves[0]);
            }
            var propertyLinks = catalog.Hydraulics.GroupBy(h => h.Field(0)).ToDictionary(g => g.Key, g => g.ToList());
            var result = new List<HydcalcSelection>();
            var seenCz = new HashSet<string>();
            foreach (var property in catalog.Properties)
            {
                double maxSpeed, minSpeed;
                int maxStages, minStages;
                if (!Number(property.Field(3), out maxSpeed) || !Number(property.Field(4), out minSpeed) ||
                    !int.TryParse(property.Field(10), out maxStages) || !int.TryParse(property.Field(11), out minStages) ||
                    minStages < 1 || maxStages < minStages || minSpeed <= 0 || maxSpeed < minSpeed) continue;
                double limit;
                if (duty.TemperatureC.HasValue &&
                    ((Number(property.Field(5), out limit) && duty.TemperatureC.Value > limit) ||
                     (Number(property.Field(6), out limit) && duty.TemperatureC.Value < limit))) continue;
                List<HydcalcRecord> links;
                if (!propertyLinks.TryGetValue(property.Id, out links)) continue;
                var curves = links.Select(l => { HydcalcCurve c; return eligible.TryGetValue(l.Field(1), out c) ? c : null; })
                    .Where(c => c != null).Distinct().ToList();
                var speeds = Speeds.Where(s => s.Key.Item1 == duty.Hz && (!duty.Poles.HasValue || s.Key.Item2 == duty.Poles.Value))
                    .Select(s => duty.FixedRpm ?? s.Value).Distinct();
                foreach (int rpm in speeds)
                {
                    if (rpm < minSpeed * 60 - 1e-6 || rpm > maxSpeed * 60 + 1e-6) continue;
                    foreach (var curve in curves.Where(c => !Suction(c)))
                    {
                        // Uniform stages, plus an optional linked suction impeller as the first stage.
                        var inlets = new List<HydcalcCurve> { null };
                        inlets.AddRange(curves.Where(c => Suction(c) && c.Header[3] == curve.Header[3] &&
                            c.Header[4] == curve.Header[4] && c.Header[5] == curve.Header[5]));
                        foreach (var inlet in inlets)
                        for (int stages = minStages; stages <= maxStages; stages++)
                        {
                            if (inlet != null && stages < 2) continue;
                            double minimum = curve.MinDiameterMm.Value / curve.MaxDiameterMm.Value;
                            if (inlet != null) minimum = Math.Max(minimum, inlet.MinDiameterMm.Value / inlet.MaxDiameterMm.Value);
                            double trim;
                            if (!TryDiameter(curve, inlet, stages, rpm, duty.Flow, duty.Head, minimum, out trim)) continue;
                            double actualHead, efficiency, npshr;
                            if (!TryEvaluate(curve, inlet, stages, rpm, trim, duty.Flow, options, out actualHead, out efficiency, out npshr)) continue;
                            if (duty.Npsha.HasValue && npshr * options.NpshMargin > duty.Npsha.Value + 1e-9) continue;
                            double qFactor, hFactor, effQ, effFactor;
                            HydcalcMath.TrimFactors(curve, trim, out qFactor, out hFactor, out effQ, out effFactor);
                            double qBep = Bep(curve).FlowM3h * rpm / curve.Rpm.Value * qFactor;
                            if (duty.Flow < qBep * options.MinBepRatio || duty.Flow > qBep * options.MaxBepRatio) continue;
                            if (string.Equals(property.Field(0), "CZ", StringComparison.OrdinalIgnoreCase) &&
                                !seenCz.Add(property.Raw + "|" + curve.Name + "|" + (inlet == null ? "" : inlet.Name) +
                                    "|" + rpm + "|" + stages)) continue;
                            result.Add(new HydcalcSelection { Property = property, Curve = curve, SuctionCurve = inlet,
                                Stages = stages, Rpm = rpm, DiameterRatio = trim, Efficiency = efficiency,
                                Npshr = npshr, Flow = duty.Flow, Head = actualHead, Options = options,
                                PowerKw = duty.Density * 9.81 * (duty.Flow / 3600) * actualHead / (efficiency / 100) / 1000 });
                        }
                    }
                }
            }
            return result.OrderByDescending(r => r.Efficiency).ThenBy(r => r.Property.Field(0)).ThenBy(r => r.Stages).ToList();
        }

        private static bool Number(string text, out double value) { return HydcalcCatalog.TryNumber(text, out value); }
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static bool Suction(HydcalcCurve c)
        {
            string add;
            return c.Metadata.TryGetValue("AddInfo", out add) && add.Contains("SuctionImpeller=suction");
        }
        private static bool SameCurve(HydcalcCurve first, HydcalcCurve second)
        {
            return first.Header.SequenceEqual(second.Header) &&
                first.Metadata.Count == second.Metadata.Count &&
                first.Metadata.All(pair => second.Metadata.ContainsKey(pair.Key) && second.Metadata[pair.Key] == pair.Value) &&
                first.Points.Select(p => new { p.Index, p.Raw }).SequenceEqual(second.Points.Select(p => new { p.Index, p.Raw }));
        }
        private static bool Eligible(HydcalcCurve c) { return EligibleCore(c, false); }
        private static bool EligibleIgnoringDuplicates(HydcalcCurve c) { return EligibleCore(c, true); }
        private static bool EligibleCore(HydcalcCurve c, bool exactDuplicates)
        {
            if (c.Issues.Any(issue => !exactDuplicates || !issue.StartsWith("Repeated hydraulic name '", StringComparison.Ordinal)) ||
                c.Points.Count < 4 || !c.Rpm.HasValue || c.Rpm <= 0 ||
                !c.MaxDiameterMm.HasValue || !c.MinDiameterMm.HasValue || c.MinDiameterMm <= 0 || c.MaxDiameterMm < c.MinDiameterMm ||
                c.Header.Length < 13 || c.Header[12] != "1" || (c.Header[4] != "volute" && c.Header[4] != "diffuser")) return false;
            for (int i = 1; i < c.Points.Count; i++) if (c.Points[i].FlowM3h <= c.Points[i - 1].FlowM3h) return false;
            try
            {
                double q, h, eq, e;
                HydcalcMath.TrimFactors(c, c.MinDiameterMm.Value / c.MaxDiameterMm.Value, out q, out h, out eq, out e);
                return c.Points.Any(p => p.Efficiency > 0) && c.Points.Count(p => p.Npshr > 0) >= 2;
            }
            catch (InvalidOperationException) { return false; }
        }
        public static HydcalcPoint Bep(HydcalcCurve c)
        {
            int index;
            if (c.Header.Length > 10 && int.TryParse(c.Header[10], out index))
            {
                var point = c.Points.FirstOrDefault(p => p.Index == index);
                if (point != null && point.Efficiency > 0) return point;
            }
            return c.Points.OrderByDescending(p => p.Efficiency).First();
        }
        public static bool TryDiameter(HydcalcCurve curve, HydcalcCurve inlet, int stages, int rpm,
            double flow, double requiredHead, double minimum, out double trim)
        {
            // Start at the smallest diameter with source flow coverage. For
            // non-monotonic curves, bracket every crossing and keep the largest root.
            trim = 0;
            double previousRatio = 0, previousError = 0;
            double lastInvalidRatio = minimum;
            bool previousValid = false, found = false;
            for (int i = 0; i <= 32; i++)
            {
                double ratio = minimum + (1 - minimum) * i / 32;
                double head;
                bool valid = TryHead(curve, inlet, stages, rpm, ratio, flow, out head);
                if (!valid) { previousValid = false; lastInvalidRatio = ratio; continue; }
                if (!previousValid && i > 0)
                {
                    // The flow-domain boundary can lie between scan samples.
                    // Include it in the root bracket instead of losing a valid
                    // duty near the catalog's last flow point.
                    double lo = lastInvalidRatio, hi = ratio;
                    for (int step = 0; step < 40; step++)
                    {
                        double boundaryHead, mid = (lo + hi) / 2;
                        if (TryHead(curve, inlet, stages, rpm, mid, flow, out boundaryHead)) hi = mid;
                        else lo = mid;
                    }
                    double firstHead;
                    if (TryHead(curve, inlet, stages, rpm, hi, flow, out firstHead))
                    {
                        previousRatio = hi; previousError = firstHead - requiredHead; previousValid = true;
                    }
                }
                double error = head - requiredHead;
                if (Math.Abs(error) <= 1e-7 * Math.Max(1, requiredHead)) { trim = ratio; found = true; }
                else if (previousValid && error * previousError < 0)
                {
                    double lo = previousRatio, hi = ratio, loError = previousError;
                    for (int step = 0; step < 55; step++)
                    {
                        double mid = (lo + hi) / 2, midHead;
                        if (!TryHead(curve, inlet, stages, rpm, mid, flow, out midHead)) break;
                        double midError = midHead - requiredHead;
                        if (midError * loError <= 0) hi = mid;
                        else { lo = mid; loError = midError; }
                    }
                    trim = (lo + hi) / 2; found = true;
                }
                previousRatio = ratio; previousError = error; previousValid = true;
            }
            return found;
        }
        public static bool TryHead(HydcalcCurve curve, HydcalcCurve inlet, int stages, int rpm, double ratio, double flow, out double head)
        {
            head = double.NaN;
            double mainHead;
            if (!StageHead(curve, rpm, ratio, flow, out mainHead)) return false;
            head = mainHead * (stages - (inlet == null ? 0 : 1));
            if (inlet != null)
            {
                double inletHead;
                if (!StageHead(inlet, rpm, ratio, flow, out inletHead)) return false;
                head += inletHead;
            }
            return head > 0 && Finite(head);
        }
        private static bool StageHead(HydcalcCurve curve, int rpm, double ratio, double flow, out double head)
        {
            double qFactor, hFactor, effQ, effFactor;
            HydcalcMath.TrimFactors(curve, ratio, out qFactor, out hFactor, out effQ, out effFactor);
            double speed = rpm / curve.Rpm.Value;
            if (!HydcalcMath.TryInterpolate(curve.Points, flow / (speed * qFactor), p => p.Head, out head)) return false;
            head *= speed * speed * hFactor;
            return head > 0;
        }
        public static bool TryEvaluate(HydcalcCurve curve, HydcalcCurve inlet, int stages, int rpm, double ratio,
            double flow, HydcalcSizingOptions options, out double head, out double efficiency, out double npshr)
        {
            head = efficiency = npshr = double.NaN;
            if (!TryHead(curve, inlet, stages, rpm, ratio, flow, out head)) return false;
            double mainHead, mainEfficiency;
            if (!StageHead(curve, rpm, ratio, flow, out mainHead) || !StageEfficiency(curve, rpm, ratio, flow, out mainEfficiency)) return false;
            double headOverEfficiency = (stages - (inlet == null ? 0 : 1)) * mainHead / mainEfficiency;
            if (inlet != null)
            {
                double inletHead, inletEfficiency;
                if (!StageHead(inlet, rpm, ratio, flow, out inletHead) || !StageEfficiency(inlet, rpm, ratio, flow, out inletEfficiency)) return false;
                headOverEfficiency += inletHead / inletEfficiency;
            }
            efficiency = head / headOverEfficiency;
            var first = inlet ?? curve;
            double speed = rpm / first.Rpm.Value, npshRatio = options.WithNpshCutting ? ratio : 1;
            if (!HydcalcMath.TryInterpolate(first.Points, flow / (speed * npshRatio), p => p.Npshr, out npshr, true)) return false;
            npshr *= speed * speed * npshRatio * npshRatio;
            return Finite(efficiency) && efficiency > 0 && efficiency <= 100 && npshr > 0;
        }
        private static bool StageEfficiency(HydcalcCurve curve, int rpm, double ratio, double flow, out double efficiency)
        {
            double qFactor, hFactor, effQ, effFactor;
            HydcalcMath.TrimFactors(curve, ratio, out qFactor, out hFactor, out effQ, out effFactor);
            if (!HydcalcMath.TryInterpolate(curve.Points, flow / (rpm / curve.Rpm.Value * effQ), p => p.Efficiency, out efficiency)) return false;
            efficiency *= effFactor;
            return efficiency > 0 && efficiency <= 100;
        }
    }
}
