using System;
using System.Collections.Generic;
using System.Linq;

namespace PumpSelectionTools
{
    // Managed translations of the arithmetic in hydcalc/HydCalc.exe.
    // Addresses and differential test procedure are documented in Tests.
    public static class HydcalcMath
    {
        public static double SulzerFactor(double original, double target)
        {
            if (original <= 0 || target <= 0) throw new ArgumentOutOfRangeException("diameter");
            if (original == target) return 1;
            double x = 100 / target, exponent;
            if (x < 1) exponent = 3;
            else if (x > 6) exponent = 2;
            else if ((int)x == 1) exponent = Lerp(x, 1, 2, 2.8, 2.63);
            else if ((int)x == 2) exponent = Lerp(x, 2, 3, 2.63, 2.45);
            else if ((int)x == 3) exponent = Lerp(x, 3, 4, 2.45, 2.3);
            else if ((int)x == 4) exponent = Lerp(x, 5, 6, 2.3, 2.18);
            else exponent = Lerp(x, 1, 2, 2.18, 2.1);
            return Math.Pow(target / original, exponent);
        }
        private static double Lerp(double x, double x0, double x1, double y0, double y1)
        {
            double t = Math.Max(0, Math.Min(1, (x - x0) / (x1 - x0)));
            return y0 + t * (y1 - y0);
        }

        public static void TrimFactors(HydcalcCurve curve, double diameterRatio,
            out double qFactor, out double hFactor, out double efficiencyFlowFactor, out double efficiencyFactor)
        {
            string text;
            if (!curve.Metadata.TryGetValue("TrimInfo", out text)) throw new InvalidOperationException("Missing trim method.");
            string[] fields = text.Split(';');
            double[] c = new double[8];
            for (int i = 0; i < c.Length; i++)
                if (fields.Length <= i + 1 || !HydcalcCatalog.TryNumber(fields[i + 1], out c[i]))
                    throw new InvalidOperationException("Invalid trim coefficients.");
            string method = fields[0];
            double r = diameterRatio;
            efficiencyFactor = 1;
            if (method == "BIT" || method == "PST")
            {
                qFactor = (1 + c[0]) * r - c[0];
                double h = (1 + c[1]) * r - c[1];
                hFactor = h * h;
                efficiencyFlowFactor = qFactor;
                if (method == "PST")
                {
                    double qSum = c[2] + c[3] + c[4], eSum = c[5] + c[6] + c[7];
                    if (Math.Abs(qSum) < 1e-12 || Math.Abs(eSum) < 1e-12)
                        throw new InvalidOperationException("Zero PST coefficient sum.");
                    efficiencyFlowFactor = (c[2] + c[3] * r + c[4] * r * r) / qSum;
                    efficiencyFactor = (c[5] + c[6] * r + c[7] * r * r) / eSum;
                }
            }
            else if (method == "Sulzer")
            {
                // The reference calculator receives SI diameters from the curve.
                double d = curve.MaxDiameterMm.Value / 1000;
                qFactor = hFactor = SulzerFactor(d, d * r);
                efficiencyFlowFactor = qFactor;
            }
            else throw new InvalidOperationException("Unsupported trim method: " + method);
            if (qFactor <= 0 || hFactor <= 0 || efficiencyFlowFactor <= 0 || efficiencyFactor <= 0)
                throw new InvalidOperationException("Trim factors are not positive.");
        }

        // Hydcalc uses a local four-point Lagrange polynomial, not a global fit.
        // Domain checks deliberately prevent extrapolation beyond source coverage.
        public static bool TryInterpolate(IList<HydcalcPoint> points, double flow, Func<HydcalcPoint, double> value,
            out double result, bool positiveOnly = false)
        {
            result = double.NaN;
            int first = 0, last = points.Count - 1;
            if (positiveOnly)
            {
                while (first <= last && value(points[first]) <= 0) first++;
                while (last >= first && value(points[last]) <= 0) last--;
            }
            if (first >= last || flow < points[first].FlowM3h - 1e-8 || flow > points[last].FlowM3h + 1e-8) return false;
            flow = Math.Max(points[first].FlowM3h, Math.Min(points[last].FlowM3h, flow));
            int lower = first;
            while (lower < last && points[lower + 1].FlowM3h <= flow) lower++;
            if (Math.Abs(points[lower].FlowM3h - flow) <= 1e-9)
            {
                result = value(points[lower]); return !positiveOnly || result > 0;
            }
            int count = Math.Min(4, last - first + 1);
            int start = Math.Max(first, Math.Min(last - count + 1, lower - 1));
            result = 0;
            for (int i = start; i < start + count; i++)
            {
                if (positiveOnly && value(points[i]) <= 0) return false;
                double term = value(points[i]);
                for (int j = start; j < start + count; j++)
                {
                    if (i == j) continue;
                    double dx = points[i].FlowM3h - points[j].FlowM3h;
                    if (Math.Abs(dx) < 1e-12) return false;
                    term *= (flow - points[j].FlowM3h) / dx;
                }
                result += term;
            }
            return !double.IsNaN(result) && !double.IsInfinity(result) && (!positiveOnly || result > 0);
        }
    }
}
