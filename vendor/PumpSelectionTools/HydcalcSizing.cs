using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace PumpSelectionTools
{
    public sealed class HydcalcDuty
    {
        public double Flow, Head, Density = 1000;
        public double ViscosityCst = 1;
        public double? Npsha, TemperatureC;
        public int Hz = 50;
        public int? Poles;
        public int? FixedRpm;
        public int? Stages;
        public HydcalcClearance? Clearance;
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
        public double ViscosityCst { get; internal set; } = 1;
        public HydcalcSizingOptions Options { get; internal set; }
        public PdfTrimCurves PdfReference { get; internal set; }
        public string NpshrSource { get; internal set; }
        public StageRange StageLimits => PdfReference is { ReferenceStages: null } ? new(Stages, Stages) :
            Property.StageLimits ?? new StageRange(Stages, Stages);
        public (double Flow, double Head, double Efficiency, double Npsh)? ViscosityFactors()
        {
            if (PdfReference != null) return null;
            if (!HydcalcSizing.TryCorrection(Curve, Rpm, DiameterRatio, ViscosityCst, out var stage) ||
                !HydcalcSizing.TryNpshCorrection(SuctionCurve ?? Curve, Rpm, DiameterRatio, Options, ViscosityCst, out var cn))
                return null;
            return (stage.Flow, stage.Head(Flow), stage.Efficiency, cn);
        }
        public string HydraulicLabel => Curve.CurveNumber +
            (SuctionCurve == null ? "" : " + suction " + SuctionCurve.CurveNumber);
        public string OriginalHydraulicLabel => Curve.OriginalCurveNumber +
            (SuctionCurve == null ? "" : " + suction " + SuctionCurve.OriginalCurveNumber);
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
        private static readonly ConditionalWeakTable<HydcalcCurve, HydcalcPoint> BepPoints = new();
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
                !Finite(duty.Density) || duty.Density <= 0 || !Finite(duty.ViscosityCst) || duty.ViscosityCst <= 0 ||
                duty.ViscosityCst > 3000 || (duty.Hz != 50 && duty.Hz != 60) ||
                (duty.FixedRpm.HasValue && duty.FixedRpm.Value <= 0) || duty.Stages is <= 0 ||
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
                if (!Number(property.Field(3), out maxSpeed) || !Number(property.Field(4), out minSpeed) ||
                    property.StageLimits is not StageRange limits ||
                    (duty.Stages is int requested && !limits.Contains(requested)) ||
                    minSpeed <= 0 || maxSpeed < minSpeed) continue;
                int minStages = duty.Stages ?? limits.Minimum, maxStages = duty.Stages ?? limits.Maximum;
                double limit;
                if (duty.TemperatureC.HasValue &&
                    ((Number(property.Field(5), out limit) && duty.TemperatureC.Value > limit) ||
                     (Number(property.Field(6), out limit) && duty.TemperatureC.Value < limit))) continue;
                List<HydcalcRecord> links;
                if (!propertyLinks.TryGetValue(property.Id, out links)) continue;
                var curves = links.Select(l => { HydcalcCurve c; return eligible.TryGetValue(l.Field(1), out c) ? c : null; })
                    .Where(c => c != null && (duty.Clearance == null || c.Clearance == duty.Clearance)).Distinct().ToList();
                foreach (int rpm in DutySpeeds(duty, minSpeed, maxSpeed))
                {
                    if (rpm < minSpeed * 60 - 1e-6 || rpm > maxSpeed * 60 + 1e-6) continue;
                    foreach (var curve in curves.Where(c => !Suction(c)))
                    {
                        if (PdfTrimCurves.For(curve.CurveNumber) is PdfTrimCurves reference)
                        {
                            if (limits.Contains(reference.Stages) &&
                                FromPdf(property, curve, reference, duty, options, rpm) is HydcalcSelection imported)
                                result.Add(imported);
                            continue;
                        }
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
                            if (!TryDiameter(curve, inlet, stages, rpm, duty.Flow, duty.Head, minimum, out trim, duty.ViscosityCst)) continue;
                            double actualHead, efficiency, npshr;
                            if (!TryEvaluate(curve, inlet, stages, rpm, trim, duty.Flow, options, out actualHead, out efficiency, out npshr, duty.ViscosityCst)) continue;
                            if (duty.Npsha.HasValue && npshr * options.NpshMargin > duty.Npsha.Value + 1e-9) continue;
                            double qFactor, hFactor, effQ, effFactor;
                            HydcalcMath.TrimFactors(curve, trim, out qFactor, out hFactor, out effQ, out effFactor);
                            double qBep = Bep(curve).FlowM3h * rpm / curve.Rpm.Value * qFactor * FlowCorrection(curve, rpm, trim, duty.ViscosityCst);
                            if (duty.Flow < qBep * options.MinBepRatio || duty.Flow > qBep * options.MaxBepRatio) continue;
                            if (string.Equals(property.Field(0), "CZ", StringComparison.OrdinalIgnoreCase) &&
                                !seenCz.Add(property.Raw + "|" + curve.Name + "|" + (inlet == null ? "" : inlet.Name) +
                                    "|" + rpm + "|" + stages)) continue;
                            result.Add(new HydcalcSelection { Property = property, Curve = curve, SuctionCurve = inlet,
                                Stages = stages, Rpm = rpm, DiameterRatio = trim, Efficiency = efficiency,
                                Npshr = npshr, Flow = duty.Flow, Head = actualHead, Options = options, ViscosityCst = duty.ViscosityCst,
                                PowerKw = duty.Density * 9.81 * (duty.Flow / 3600) * actualHead / (efficiency / 100) / 1000 });
                        }
                    }
                }
            }
            return result.OrderByDescending(r => r.Efficiency).ThenBy(r => r.Property.Field(0)).ThenBy(r => r.Stages).ToList();
        }

        internal static HydcalcSelection FromPdf(HydcalcRecord property, HydcalcCurve curve, PdfTrimCurves reference,
            HydcalcDuty duty, HydcalcSizingOptions options, int rpm)
        {
            if ((duty.Stages is int stages && stages != reference.Stages) ||
                (reference.MinimumFlowM3h is double minimum && duty.Flow < minimum) ||
                (duty.Clearance != null && curve.Clearance != duty.Clearance))
                return null;
            // The original catalog entry limits this digitized family to its specified operating speed.
            var diameter = duty.ViscosityCst <= 1 ? reference.DiameterAtDuty(rpm, duty.Flow, duty.Head) : null;
            if (diameter is not double trim) return null;
            var rated = reference.Sample(curve, rpm, trim, duty.Flow);
            if (rated.Head is not (> 0) || rated.Efficiency is not (> 0 and <= 100) ||
                rated.Npshr is not (> 0) || rated.WaterPower is not (> 0) ||
                (duty.Npsha is double available && rated.Npshr.Value * options.NpshMargin > available + 1e-9))
                return null;
            if (reference.UsesPlottedEfficiency &&
                Math.Abs(rated.Efficiency.Value - 100 * 9.81 * duty.Flow * rated.Head.Value /
                    (3600 * rated.WaterPower.Value)) > 6)
                return null;
            var bep = reference.Samples(curve, rpm, trim, duty.Flow)
                .Where(sample => sample.Efficiency is > 0 and <= 100 && sample.Head is > 0 && sample.WaterPower is > 0)
                .MaxBy(sample => sample.Efficiency);
            if (duty.Flow < bep.Flow * options.MinBepRatio || duty.Flow > bep.Flow * options.MaxBepRatio)
                return null;
            return new HydcalcSelection
            {
                Property = property, Curve = curve, Stages = reference.Stages, Rpm = rpm, DiameterRatio = trim / reference.MaxDiameter,
                Efficiency = rated.Efficiency.Value, Npshr = rated.Npshr.Value, Flow = duty.Flow, Head = rated.Head.Value,
                PowerKw = rated.WaterPower.Value * duty.Density / 1000, Options = options, ViscosityCst = duty.ViscosityCst,
                PdfReference = reference, NpshrSource = rated.NpshrSource
            };
        }

        internal static bool MatchesPublishedSpeed(HydcalcDuty duty, int rpm)
        {
            if (duty.FixedRpm.HasValue && !duty.Poles.HasValue) return true;
            return Speeds.Keys.Any(s => s.Item1 == duty.Hz && (!duty.Poles.HasValue || s.Item2 == duty.Poles) &&
                rpm >= 0.9 * (120d * duty.Hz / s.Item2) && rpm <= 1.02 * (120d * duty.Hz / s.Item2));
        }

        private static bool Number(string text, out double value) { return HydcalcCatalog.TryNumber(text, out value); }
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static IEnumerable<int> DutySpeeds(HydcalcDuty duty, double minSpeed, double maxSpeed)
        {
            var motors = Speeds.Where(s => s.Key.Item1 == duty.Hz && (!duty.Poles.HasValue || s.Key.Item2 == duty.Poles.Value));
            if (minSpeed != maxSpeed || duty.FixedRpm.HasValue)
                return motors.Select(s => duty.FixedRpm ?? s.Value).Distinct();

            // A catalog restricted to one speed uses its recorded RPM, not the generic motor RPM.
            int rpm = (int)Math.Round(minSpeed * 60);
            return motors.Any(s =>
            {
                double synchronous = 120.0 * duty.Hz / s.Key.Item2;
                return rpm >= .90 * synchronous && rpm <= 1.02 * synchronous;
            }) ? new[] { rpm } : Array.Empty<int>();
        }
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
        public static HydcalcPoint Bep(HydcalcCurve c) => BepPoints.GetValue(c, FindBep);

        private static HydcalcPoint FindBep(HydcalcCurve c)
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
            double flow, double requiredHead, double minimum, out double trim, double viscosityCst = 1)
        {
            // Start at the smallest diameter with source flow coverage. For
            // non-monotonic curves, bracket every crossing and keep the largest root.
            trim = 0;
            if (minimum == 1 && curve.Metadata.TryGetValue("TrimInfo", out var method) && method == "Fixed")
            {
                // Match the published head to its 0.01 m resolution without interpolating a trim.
                if (!TryHead(curve, inlet, stages, rpm, 1, flow, out var head, viscosityCst) ||
                    Math.Abs(head - requiredHead) > .005 + 1e-9) return false;
                trim = 1;
                return true;
            }
            double previousRatio = 0, previousError = 0;
            double lastInvalidRatio = minimum;
            bool previousValid = false, found = false;
            for (int i = 0; i <= 32; i++)
            {
                double ratio = minimum + (1 - minimum) * i / 32;
                double head;
                bool valid = TryHead(curve, inlet, stages, rpm, ratio, flow, out head, viscosityCst);
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
                        if (TryHead(curve, inlet, stages, rpm, mid, flow, out boundaryHead, viscosityCst)) hi = mid;
                        else lo = mid;
                    }
                    double firstHead;
                    if (TryHead(curve, inlet, stages, rpm, hi, flow, out firstHead, viscosityCst))
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
                        if (!TryHead(curve, inlet, stages, rpm, mid, flow, out midHead, viscosityCst)) break;
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
        public static bool TryHead(HydcalcCurve curve, HydcalcCurve inlet, int stages, int rpm, double ratio, double flow, out double head,
            double viscosityCst = 1)
        {
            head = double.NaN;
            double mainHead;
            if (!StageHead(curve, rpm, ratio, flow, viscosityCst, out mainHead)) return false;
            head = mainHead * (stages - (inlet == null ? 0 : 1));
            if (inlet != null)
            {
                double inletHead;
                if (!StageHead(inlet, rpm, ratio, flow, viscosityCst, out inletHead)) return false;
                head += inletHead;
            }
            return head > 0 && Finite(head);
        }
        private static bool StageHead(HydcalcCurve curve, int rpm, double ratio, double flow, double viscosityCst, out double head)
        {
            double qFactor, hFactor, effQ, effFactor;
            HydcalcMath.TrimFactors(curve, ratio, out qFactor, out hFactor, out effQ, out effFactor);
            double speed = rpm / curve.Rpm.Value;
            head = double.NaN;
            if (!StageCorrection(curve, rpm, speed, qFactor, hFactor, viscosityCst, out var correction) ||
                !HydcalcMath.TryInterpolate(curve.Points, flow / (speed * qFactor * correction.Flow), p => p.Head, out head))
                return false;
            head *= speed * speed * hFactor * correction.Head(flow);
            return head > 0 && Finite(head);
        }
        public static bool TryEvaluate(HydcalcCurve curve, HydcalcCurve inlet, int stages, int rpm, double ratio,
            double flow, HydcalcSizingOptions options, out double head, out double efficiency, out double npshr, double viscosityCst = 1)
        {
            head = efficiency = npshr = double.NaN;
            if (!TryHead(curve, inlet, stages, rpm, ratio, flow, out head, viscosityCst)) return false;
            double mainHead, mainEfficiency;
            if (!StageHead(curve, rpm, ratio, flow, viscosityCst, out mainHead) ||
                !StageEfficiency(curve, rpm, ratio, flow, viscosityCst, out mainEfficiency)) return false;
            double headOverEfficiency = (stages - (inlet == null ? 0 : 1)) * mainHead / mainEfficiency;
            if (inlet != null)
            {
                double inletHead, inletEfficiency;
                if (!StageHead(inlet, rpm, ratio, flow, viscosityCst, out inletHead) ||
                    !StageEfficiency(inlet, rpm, ratio, flow, viscosityCst, out inletEfficiency)) return false;
                headOverEfficiency += inletHead / inletEfficiency;
            }
            efficiency = head / headOverEfficiency;
            return TryNpshr(inlet ?? curve, rpm, ratio, flow, options, viscosityCst, out npshr) &&
                Finite(efficiency) && efficiency > 0 && efficiency <= 100;
        }

        internal static bool TryNpshr(HydcalcCurve first, int rpm, double ratio, double flow,
            HydcalcSizingOptions options, double viscosityCst, out double npshr)
        {
            double speed = rpm / first.Rpm.Value, npshRatio = options.WithNpshCutting ? ratio : 1;
            double npshScale = speed * npshRatio;
            npshr = double.NaN;
            if (!TryNpshCorrection(first, rpm, ratio, options, viscosityCst, out var cn) ||
                !HydcalcMath.TryInterpolate(first.Points, flow / npshScale, p => p.Npshr, out npshr, true)) return false;
            npshr *= speed * speed * npshRatio * npshRatio * cn;
            return npshr > 0 && Finite(npshr);
        }

        internal static bool TryNpshCorrection(HydcalcCurve first, int rpm, double ratio, HydcalcSizingOptions options,
            double viscosityCst, out double cn)
        {
            cn = 1;
            if (viscosityCst <= 1) return true;
            if (!TryCorrection(first, rpm, ratio, viscosityCst, out var correction)) return false;
            if (correction.Flow == 1) return true;
            double scale = rpm / first.Rpm.Value * (options.WithNpshCutting ? ratio : 1);
            if (!HydcalcMath.TryInterpolate(first.Points, correction.WaterBepFlow / scale, p => p.Npshr, out var waterBep, true))
                return false;
            cn = correction.Npsh(waterBep * scale * scale, rpm);
            return Finite(cn) && cn >= 1;
        }

        internal static double FlowCorrection(HydcalcCurve curve, int rpm, double ratio, double viscosityCst) =>
            TryCorrection(curve, rpm, ratio, viscosityCst, out var correction) ? correction.Flow : double.NaN;

        internal static bool TryCorrection(HydcalcCurve curve, int rpm, double ratio, double viscosityCst, out HydcalcViscosity correction)
        {
            if (viscosityCst <= 1) return HydcalcViscosity.TryCreate(viscosityCst, 0, 0, rpm, out correction);
            HydcalcMath.TrimFactors(curve, ratio, out var q, out var h, out _, out _);
            return StageCorrection(curve, rpm, rpm / curve.Rpm.Value, q, h, viscosityCst, out correction);
        }

        private static bool StageCorrection(HydcalcCurve curve, int rpm, double speed, double q, double h,
            double viscosityCst, out HydcalcViscosity correction)
        {
            if (viscosityCst <= 1) return HydcalcViscosity.TryCreate(viscosityCst, 0, 0, rpm, out correction);
            var bep = Bep(curve);
            return HydcalcViscosity.TryCreate(viscosityCst, bep.FlowM3h * speed * q, bep.Head * speed * speed * h, rpm, out correction);
        }

        private static bool StageEfficiency(HydcalcCurve curve, int rpm, double ratio, double flow, double viscosityCst, out double efficiency)
        {
            double qFactor, hFactor, effQ, effFactor;
            HydcalcMath.TrimFactors(curve, ratio, out qFactor, out hFactor, out effQ, out effFactor);
            double speed = rpm / curve.Rpm.Value;
            efficiency = double.NaN;
            if (!StageCorrection(curve, rpm, speed, qFactor, hFactor, viscosityCst, out var correction) ||
                !HydcalcMath.TryInterpolate(curve.Points, flow / (speed * effQ * correction.Flow), p => p.Efficiency, out efficiency))
                return false;
            efficiency *= effFactor * correction.Efficiency;
            return efficiency > 0 && efficiency <= 100;
        }
    }
}
