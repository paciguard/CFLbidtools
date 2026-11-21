using System;

namespace PumpSelectionTools
{
    internal readonly record struct HydcalcViscosity(double WaterBepFlow, double Flow, double Efficiency)
    {
        internal double Head(double flow) =>
            Flow == 1 ? 1 : 1 - (1 - Flow) * Math.Pow(flow / (Flow * WaterBepFlow), .75);

        // ANSI/HI 9.6.7-2021 metric equations 2, 4, 6 and 7. Use the water BEP of each hydraulic stage.
        internal static bool TryCreate(double cst, double flow, double head, int rpm, out HydcalcViscosity factors)
        {
            factors = new HydcalcViscosity(flow, 1, 1);
            if (cst <= 1) return true;
            if (flow <= 0 || head <= 0 || rpm <= 0) return false;
            var b = 16.5 * Math.Sqrt(cst) * Math.Pow(head, .0625) / (Math.Pow(flow, .375) * Math.Pow(rpm, .25));
            if (!double.IsFinite(b) || b >= 40 || rpm * Math.Sqrt(flow / 3600) / Math.Pow(head, .75) > 60)
                return false;
            if (b > 1)
                factors = new HydcalcViscosity(flow, Math.Pow(2.71, -.165 * Math.Pow(Math.Log10(b), 3.15)),
                    Math.Pow(b, -.0547 * Math.Pow(b, .69)));
            return true;
        }

        // Equation 14. Use the conservative side-inlet coefficient where the catalog does not give inlet geometry.
        internal double Npsh(double waterNpshAtBep, int rpm) =>
            Flow == 1 ? 1 : 1 + .5 * (1 / Flow - 1) * 274000 * waterNpshAtBep /
                (Math.Pow(WaterBepFlow, .667) * Math.Pow(rpm, 1.33));
    }
}
