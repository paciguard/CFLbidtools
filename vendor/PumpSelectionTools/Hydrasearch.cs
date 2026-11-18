using OxyPlot.Series;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;


namespace PumpSelectionTools
{
    
    public class PumpData
    {
        public string Type { get; set; }
        public string Size { get; set; }
        public double[] Flow { get; set; }
        public double OrigRPM { get; set; }
        public double[] Head { get; set; }
        public double[] Efficiency { get; set; }
        public double[] NPSHr { get; set; }
        public double[] NPSHrY { get; set; }
        public double FlowBEP { get; set; }
        public double HeadBEP { get; set; }
        public double EfficiencyBEP { get; set; }
        public double NPSHrBEP { get; set; }
        public double QmaxEnd { get; set; }
        public double Clearance { get; set; }
    }
    public class ZDiaRange
    {
        public int MaxDia { get; set; }
        public int MinDia { get; set; }

        public ZDiaRange(int maxDia, int minDia)
        {
            MaxDia = maxDia;
            MinDia = minDia;
        }
        
       

    }
    public static class ZDiaTable
    {
        private static readonly Dictionary<string, ZDiaRange> diaDict =
            new Dictionary<string, ZDiaRange>
            {
            { "160", new ZDiaRange(169, 125) },
            { "200", new ZDiaRange(209, 160) },
            { "250", new ZDiaRange(259, 200) },
            { "315", new ZDiaRange(324, 250) },
            { "400", new ZDiaRange(409, 320) },
            { "450", new ZDiaRange(458, 350) },
            { "500", new ZDiaRange(514, 400) },
            { "401", new ZDiaRange(409, 320) },
            { "560", new ZDiaRange(578, 450) },
            { "630", new ZDiaRange(648, 500) },
            { "360", new ZDiaRange(359, 290) },
            { "720", new ZDiaRange(728, 600) },
            { "728", new ZDiaRange(725, 600) }
            };

        public static ZDiaRange GetDiaRange(string pumpSize)
        {
            if (string.IsNullOrWhiteSpace(pumpSize))
                throw new ArgumentException("Pump size must not be empty.", nameof(pumpSize));

            ZDiaRange range50_450 = new ZDiaRange(458, 380);

            if (pumpSize == "50-450")
                return range50_450;

            string[] parts = pumpSize.Split('-');
            if (parts.Length < 2)
                throw new ArgumentException(
                    $"Pump size '{pumpSize}' is not in the expected 'NN-MMM' format.",
                    nameof(pumpSize));

            if (!diaDict.TryGetValue(parts[1], out ZDiaRange range))
                throw new ArgumentException($"Pump size {pumpSize} not defined.");

            return range;
        }
    }

    public class PumpSizeResult
    {
        public string PumpSize { get; set; }
        public double ImpellerDiameter { get; set; }   // mm
        public int PumpRpm { get; set; }
    }


    internal class Hydrasearch
    {

        
    }
    public class CachedPumpCurve
    {
        public QuadraticFitter HeadDiaMaxFit;
        public QuadraticFitter HeadDiaMinFit;
        public QuadraticFitterNpsh NpshFit;
        public QuadraticFitterEq EffFit;
        public QuadraticFitter NsMaxDiaFit;
        public QuadraticFitter NsMinDiaFit;

    }
    public static class PumpCurveCache
    {
        public static readonly Dictionary<string, CachedPumpCurve> Cache
            = new Dictionary<string, CachedPumpCurve>();
    }

    // Loads every pump curve from the embedded Excel once at startup.
    // GetPumpCurve then becomes a pure dictionary lookup — no file I/O per search.
    public static class PumpCurveDataCache
    {
        private static readonly Dictionary<(string type, string size), PumpData> _cache
            = new Dictionary<(string type, string size), PumpData>();
        private static bool _loaded = false;

        public static void Load()
        {
            if (_loaded) return;

            using (var stream = new MemoryStream(Properties.Resources.SulzerCurveData))
            using (var workbook = NPOI.SS.UserModel.WorkbookFactory.Create(stream))
            {
                var ws = workbook.GetSheet("Curve");
                if (ws == null) return;

                string Cell(int row, int column) => CatalogWorkbook.Cell(ws, row, column);
                int totalRows      = ws.LastRowNum + 1;
                const int stride   = 41; // 1 header + 40 data rows per pump block

                for (int r = 2; r <= totalRows; r += stride)
                {
                    if (Cell(r, 4)?.Trim() != "HydrInfo") continue;

                    string typeCell = Cell(r, 5)?.Trim();
                    string sizeCell = Cell(r, 7)?.Trim();
                    if (string.IsNullOrEmpty(typeCell) || string.IsNullOrEmpty(sizeCell)) continue;

                    double.TryParse(Cell(r, 16)?.Trim(), out double rpmCell);
                    double.TryParse(Cell(r, 21)?.Trim(), out double flowBep);
                    double.TryParse(Cell(r, 22)?.Trim(), out double headBep);
                    double.TryParse(Cell(r, 23)?.Trim(), out double efficBep);
                    double.TryParse(Cell(r, 24)?.Trim(), out double npshrBep);

                    double hzKey  = Math.Round(rpmCell, 5);
                    double pumpRPM = CentrifugalSearch.HzToRpmO.TryGetValue(hzKey, out int rpmO) ? rpmO : rpmCell;

                    var flow  = new double[40];
                    var head  = new double[40];
                    var eff   = new double[40];
                    var npshr = new double[40];
                    var npshY = new double[40];
                    var q0q   = new double[40];

                    for (int i = 0; i < 40; i++)
                    {
                        int row  = r + 1 + i;
                        flow[i]  = 3600 * (double.TryParse(Cell(row, 5),  out double f)  ? f  : 0);
                        head[i]  =         double.TryParse(Cell(row, 7),  out double h)  ? h  : 0;
                        eff[i]   =         double.TryParse(Cell(row, 12), out double e)  ? e  : 0;
                        q0q[i]   =         double.TryParse(Cell(row, 11), out double q)  ? q  : 0;
                        npshr[i] =         double.TryParse(Cell(row, 13), out double nx) ? nx : 0;
                        npshY[i] =         double.TryParse(Cell(row, 16), out double ny) ? ny : 0;
                    }

                    // -------------------------------------------------------------
                    // Data-quality guard for NPSHr column:
                    // For a small number of catalog entries (e.g. Z 25-250) the
                    // NPSHr-X column (col 13) has been populated with the Q0-Q flow
                    // values rather than the true NPSHr-Y values, leaving the real
                    // NPSHr curve only in col 16. Detect that case and fall back
                    // to col 16 so the affinity / cutting-law correction in
                    // foundPsize produces physically meaningful numbers instead of
                    // values proportional to flow.
                    // -------------------------------------------------------------
                    bool npshrLooksLikeFlow = true;
                    bool anyNpshrValue      = false;
                    for (int i = 0; i < 40; i++)
                    {
                        if (npshr[i] != 0) anyNpshrValue = true;
                        if (Math.Abs(npshr[i] - q0q[i]) > 1e-9)
                        {
                            npshrLooksLikeFlow = false;
                            break;
                        }
                    }
                    if (anyNpshrValue && npshrLooksLikeFlow)
                    {
                        // col 13 is corrupted with flow values — use col 16 directly.
                        // (Most pumps store NPSHr×10 in col 16; for the broken rows
                        // col 16 contains the un-scaled true NPSHr in metres, which
                        // matches the BEP-NPSHr value from HydraulicTable.)
                        for (int i = 0; i < 40; i++)
                            npshr[i] = npshY[i];
                    }

                    _cache[(typeCell, sizeCell)] = new PumpData
                    {
                        Type          = typeCell,
                        Size          = sizeCell,
                        Flow          = flow,
                        Head          = head,
                        Efficiency    = eff,
                        NPSHr         = npshr,
                        NPSHrY        = npshY,
                        OrigRPM       = pumpRPM,
                        FlowBEP       = flowBep,
                        HeadBEP       = headBep,
                        EfficiencyBEP = efficBep,
                        NPSHrBEP      = npshrBep
                    };
                }
            }
            _loaded = true;
        }

        public static PumpData Get(string pumpType, string pumpSize)
        {
            _cache.TryGetValue((pumpType, pumpSize), out var data);
            return data;
        }
    }

    public class PumpRow
    {
        public string PumpType;
        public string PumpSize;
        //All the varialbe for Max impeller data
        public double BepFlow;
        public double Head;
        public double Npsh;
        public double Z;//BEP efficiency
        public double MinFlow;//Minimum Stable Flow @1475rpm
        public int Poles;
        public int Hz;
        public int Rpm;
        public int AOP1,AOP2,AOP3;
        public double BepE;
        public double QmaxEnd; 
        public double Hmax;
        public double Hmin;
        public double Clearance;

    }
    public static class PumpTableCache
    {
        public static readonly List<PumpRow> Rows = new List<PumpRow>();

        public static void Load()
        {
            if (Rows.Count > 0)
                return;

            using (var ms = new MemoryStream(Properties.Resources.HydraulicTable))
            using (var workbook = NPOI.SS.UserModel.WorkbookFactory.Create(ms))
            {
                var ws = workbook.GetSheet("Z");
                if (ws == null)
                    return;

                string Cell(int row, int column) => CatalogWorkbook.Cell(ws, row, column);
                int lastRow = ws.LastRowNum + 1;

                for (int r = 2; r <= lastRow; r++)
                {
                    if (!double.TryParse(Cell(r, 34), out double bep))
                        continue;

                    // Defensive parses: a single empty or malformed cell must not
                    // abort loading of the whole pump table. Missing numerics
                    // default to 0.
                    double D(int col) { double.TryParse(Cell(r, col), out double v); return v; }
                    int    I(int col) { int.TryParse(Cell(r, col), out int v);    return v; }

                    Rows.Add(new PumpRow
                    {
                        PumpType  = Cell(r, 4),
                        PumpSize  = Cell(r, 6),
                        Clearance = D(16),
                        BepFlow   = bep,                 // already parsed above
                        Hmax      = D(30),
                        Hmin      = D(25),
                        Head      = D(28),
                        Npsh      = D(29),
                        Z         = D(26),
                        MinFlow   = D(41),
                        Poles     = I(36),
                        Hz        = I(37),
                        Rpm       = I(33),
                        AOP1      = I(38),
                        AOP2      = I(39),
                        AOP3      = I(40),
                        QmaxEnd   = D(35),
                    });
                }
            }
        }
    }



    public static class HydcalcZSelection
    {
        private static readonly Dictionary<string, HydcalcCurve> CurveCache = new Dictionary<string, HydcalcCurve>(StringComparer.OrdinalIgnoreCase);

        public static HydcalcCurve MatchCurve(string size)
        {
            if (string.IsNullOrWhiteSpace(size)) return null;
            HydcalcCurve cached;
            if (CurveCache.TryGetValue(size, out cached)) return cached;
            var data = CentrifugalSearch.GetPumpCurve("Z", size);
            if (data == null || data.Flow == null || data.Head == null || data.Flow.Length != data.Head.Length)
                return null;
            var matches = HydcalcCatalog.Bundled.Curves.Where(c =>
                string.Equals(c.Family, "Z", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Size, size, StringComparison.OrdinalIgnoreCase) &&
                c.Points.Count == data.Flow.Length && c.Issues.Count == 0 && c.Rpm > 0 &&
                c.MinDiameterMm > 0 && c.MaxDiameterMm >= c.MinDiameterMm &&
                c.Points.Select((p, i) => Math.Abs(p.FlowM3h - data.Flow[i]) <= 0.001 &&
                    Math.Abs(p.Head - data.Head[i]) <= 0.001).All(ok => ok)).ToList();
            cached = matches.Count == 1 ? matches[0] : null;
            CurveCache[size] = cached;
            return cached;
        }

        public static List<HydcalcSelection> Search(double flow, double head, double? npsha, int hz, int? poles, int? fixedRpm = null)
        {
            var results = new List<HydcalcSelection>();
            if (flow <= 0 || head <= 0 || double.IsNaN(flow) || double.IsNaN(head) ||
                double.IsInfinity(flow) || double.IsInfinity(head) || (hz != 50 && hz != 60) ||
                (fixedRpm.HasValue && fixedRpm.Value <= 0) ||
                (npsha.HasValue && (npsha.Value < 0 || double.IsNaN(npsha.Value) || double.IsInfinity(npsha.Value))))
                return results;
            if (fixedRpm.HasValue && !poles.HasValue)
                poles = CentrifugalSearch.PoleSpeedMap.Where(p => p.Key.Hz == hz)
                    .OrderBy(p => Math.Abs(p.Value - fixedRpm.Value)).First().Key.Poles;
            PumpTableCache.Load();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in PumpTableCache.Rows)
            {
                if (!string.Equals(row.PumpType, "Z", StringComparison.OrdinalIgnoreCase) || row.Rpm <= 0) continue;
                var curve = MatchCurve(row.PumpSize);
                if (curve == null) continue;
                foreach (int pole in CentrifugalSearch.GetAllowedPoles(row).Distinct())
                {
                    int rpm;
                    if ((poles.HasValue && poles.Value != pole) || !CentrifugalSearch.PoleSpeedMap.TryGetValue((hz, pole), out rpm)) continue;
                    if (fixedRpm.HasValue) rpm = fixedRpm.Value;
                    double speedRatio = (double)rpm / row.Rpm;
                    if (flow >= 1.2 * row.BepFlow * speedRatio || flow * 1475 / rpm < row.MinFlow * 1.1 * speedRatio) continue;
                    if (!seen.Add(row.PumpSize + "@" + rpm)) continue;
                    double ratio, actualHead, efficiency, npshr;
                    var options = new HydcalcSizingOptions();
                    if (!HydcalcSizing.TryDiameter(curve, null, 1, rpm, flow, head,
                        curve.MinDiameterMm.Value / curve.MaxDiameterMm.Value, out ratio) ||
                        !HydcalcSizing.TryEvaluate(curve, null, 1, rpm, ratio, flow, options, out actualHead, out efficiency, out npshr) ||
                        (npsha.HasValue && npshr * options.NpshMargin > npsha.Value + 1e-9)) continue;
                    results.Add(new HydcalcSelection
                    {
                        Property = new HydcalcRecord { Id = "Z Excel", Fields = new[] { "Z", row.PumpSize, "", "", "", "", "", "", "", "卧式" } },
                        Curve = curve, Stages = 1, Rpm = rpm, DiameterRatio = ratio, Flow = flow, Head = actualHead,
                        Efficiency = efficiency, Npshr = npshr, PowerKw = 1000 * 9.81 * flow / 3600 * actualHead / (efficiency / 100) / 1000,
                        Options = options
                    });
                }
            }
            return results;
        }

        public static double RatedEfficiency(HydcalcSelection selection)
        {
            double efficiency, bepFlow, bepEfficiency;
            return Calculations.TryGetValidatedPerformancePoint(selection.Property.Field(1), selection.Rpm,
                selection.Flow, selection.Head, out efficiency, out bepFlow, out bepEfficiency)
                ? efficiency : selection.Efficiency;
        }
    }

    public class CentrifugalSearch
    {
        public  static readonly (int Rpm60Hz, int Rpm50Hz)[] RpmTable =
        {
            (3550, 2950),
            (1780, 1480),
            (1180, 980),
            (850,  735)
        };
        public static readonly Dictionary<(int Hz, int Poles), int> PoleSpeedMap = new Dictionary<(int Hz, int Poles), int>()
        {
            { (50, 2), 2950 },
            { (50, 4), 1480 },
            { (50, 6), 980 },
            { (50, 8), 735 },
            { (50, 10), 590 },
            { (50, 12), 490 },

            { (60, 2), 3550 },
            { (60, 4), 1780 },
            { (60, 6), 1180 },
            { (60, 8), 885 },
            { (60, 10), 710 },
            { (60, 12), 590 }
        };

        public struct RpmRecord
        {
            public double Hz;
            public int    RpmO;   // actual operating RPM
            public int    RpmTr;  // nearest standard (trimmed) RPM
            public int    RpmF;   // synchronous (full) RPM
            public int    Frequency; // nominal Hz (50 or 60)
            public int    Poles;
        }

        public static readonly RpmRecord[] RpmReferenceTable = new RpmRecord[]
        {
            new RpmRecord { Hz=49.16667, RpmO=2950, RpmTr=2950, RpmF=3000, Frequency=50, Poles=2 },
            new RpmRecord { Hz=29.58333, RpmO=1775, RpmTr=1780, RpmF=3600, Frequency=60, Poles=4 },
            new RpmRecord { Hz=59.16667, RpmO=3550, RpmTr=3550, RpmF=3600, Frequency=60, Poles=2 },
            new RpmRecord { Hz=24.5,     RpmO=1470, RpmTr=1480, RpmF=3000, Frequency=50, Poles=4 },
            new RpmRecord { Hz=16.33333, RpmO=980,  RpmTr=980,  RpmF=3000, Frequency=50, Poles=6 },
            new RpmRecord { Hz=29.66667, RpmO=1780, RpmTr=1780, RpmF=3600, Frequency=60, Poles=4 },
            new RpmRecord { Hz=59.5,     RpmO=3570, RpmTr=3550, RpmF=3600, Frequency=60, Poles=2 },
            new RpmRecord { Hz=19.75,    RpmO=1185, RpmTr=1180, RpmF=3600, Frequency=60, Poles=6 },
            new RpmRecord { Hz=59.33333, RpmO=3560, RpmTr=3550, RpmF=3600, Frequency=60, Poles=2 },
            new RpmRecord { Hz=29.33333, RpmO=1460, RpmTr=1480, RpmF=3000, Frequency=50, Poles=4 },
            new RpmRecord { Hz=19.5,     RpmO=1170, RpmTr=1180, RpmF=3600, Frequency=60, Poles=2 },
            new RpmRecord { Hz=49.66667, RpmO=2980, RpmTr=2950, RpmF=3000, Frequency=50, Poles=2 },
            new RpmRecord { Hz=48.33333, RpmO=2960, RpmTr=2950, RpmF=3000, Frequency=50, Poles=2 },
            new RpmRecord { Hz=58.66667, RpmO=3580, RpmTr=3550, RpmF=3600, Frequency=60, Poles=2 },
            new RpmRecord { Hz=19.16667, RpmO=1180, RpmTr=1180, RpmF=3600, Frequency=60, Poles=6 },
            new RpmRecord { Hz=24.83333, RpmO=1490, RpmTr=1480, RpmF=3000, Frequency=50, Poles=4 },
            new RpmRecord { Hz=24.58333, RpmO=1475, RpmTr=1480, RpmF=3000, Frequency=50, Poles=4 },
            new RpmRecord { Hz=12.25,    RpmO=735,  RpmTr=735,  RpmF=3000, Frequency=50, Poles=8 },
            new RpmRecord { Hz=24.75,    RpmO=1485, RpmTr=1480, RpmF=3000, Frequency=50, Poles=4 },
            new RpmRecord { Hz=24.66667, RpmO=1480, RpmTr=1480, RpmF=3000, Frequency=50, Poles=4 },
        };

        // Key: Hz (rounded to 5 dp), Value: RpmO — must be declared after RpmReferenceTable
        public static readonly Dictionary<double, int> HzToRpmO =
            RpmReferenceTable.ToDictionary(
                r => Math.Round(r.Hz, 5),
                r => r.RpmO);

        public static IEnumerable<int> GetAllowedPoles(PumpRow row)
        {
            if (row.AOP1 != 0) yield return row.AOP1;
            if (row.AOP2 != 0) yield return row.AOP2;
            if (row.AOP3 != 0) yield return row.AOP3;
        }

        public static PumpData GetPumpCurve(string pumpType, string pumpSize)
        {
            PumpCurveDataCache.Load();
            return PumpCurveDataCache.Get(pumpType, pumpSize);
        }

        public static int GetExpectedRpm(int rowRpm, int hz)
        {
            // Find the matching row
            foreach (var entry in RpmTable)
            {
                if (entry.Rpm60Hz == rowRpm)
                {
                    // Decide which value to return based on hz
                    if (hz == 60)
                        return entry.Rpm60Hz;
                    else if (hz == 50)
                        return entry.Rpm50Hz;
                    else
                        throw new ArgumentException("Only 50 Hz and 60 Hz are supported.");
                }
                else
                if(entry.Rpm50Hz == rowRpm)
                {
                    // Decide which value to return based on hz
                    if (hz == 60)
                        return entry.Rpm60Hz;
                    else if (hz == 50)
                        return entry.Rpm50Hz;
                    else
                        throw new ArgumentException("Only 50 Hz and 60 Hz are supported.");
                }
            }

            // Not found in table
            return 0;
        }

        public static readonly Dictionary<int, int[]> HzRpmOptions = new Dictionary<int, int[]>()
    {
        { 50, new[] { 2950, 1480, 980 } },
        { 60, new[] { 3550, 1780, 1180 } }
    };
        public static bool FindIntersection(
                                            QuadraticFitter fitA,
                                            QuadraticFitter fitB,
                                            double qMin,
                                            double qMax,
                                            out double Q,
                                            out double H)
        {
            Q = 0;
            H = 0;

            double a = qMin;
            double b = qMax;

            double fa = fitA.Evaluate(a) - fitB.Evaluate(a);
            double fb = fitA.Evaluate(b) - fitB.Evaluate(b);

            // No crossing in range
            if (fa * fb > 0)
                return false;

            for (int i = 0; i < 100; i++)
            {
                double m = 0.5 * (a + b);
                double fm = fitA.Evaluate(m) - fitB.Evaluate(m);

                if (Math.Abs(fm) < 1e-6)
                {
                    Q = m;
                    H = fitA.Evaluate(m);
                    return true;
                }

                if (fa * fm < 0)
                {
                    b = m;
                    fb = fm;
                }
                else
                {
                    a = m;
                    fa = fm;
                }
            }

            Q = 0.5 * (a + b);
            H = fitA.Evaluate(Q);
            return true;
        }

        

        public static double FlowFromEfficiency(QuadraticFitter EffFit, double effInput)
        {
            if (EffFit == null) throw new ArgumentNullException(nameof(EffFit));

            double a = EffFit.a;
            double b = EffFit.b;
            double c = EffFit.c;

            // Degenerate (near-linear) case: avoid division by 2*a ≈ 0.
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) < 1e-12)
                    throw new Exception("Efficiency curve is degenerate (a≈0 and b≈0).");
                double xLin = (effInput - c) / b;
                if (xLin > 0) return xLin;
                throw new Exception("No positive flow solution found");
            }

            // Solve a*flow^2 + b*flow + (c - effInput) = 0
            double discriminant = b * b - 4 * a * (c - effInput);

            if (discriminant < 0)
                throw new Exception("No real solution: efficiency outside curve range");

            double sqrtDisc = Math.Sqrt(discriminant);

            // Choose the positive and physically reasonable root
            double flow1 = (-b + sqrtDisc) / (2 * a);
            double flow2 = (-b - sqrtDisc) / (2 * a);

            // Typically, flow is positive
            if (flow1 > 0 && flow2 > 0)
                return Math.Min(flow1, flow2); // choose lower flow (left side of curve)
            else if (flow1 > 0)
                return flow1;
            else if (flow2 > 0)
                return flow2;
            else
                throw new Exception("No positive flow solution found");
        }
        /// <summary>
        /// search fit hydraulic pump size
        /// </summary>
        /// <param name="flowRef"></param>
        /// <param name="headRef"></param>
        /// <param name="npsha">Available NPSH at the requested operating condition.</param>
        /// <param name="hz"></param>
        /// <param name="pol"></param>
        /// <returns></returns>
        public static List<(string PumpType, string PumpSize, int Stages, int Rpm, double DiaRatio, double trimDia,
                            double Dmax, double Dmin, double Efficiency, double Npshr, double Clearance, string Orientation)> foundPsize(
                            double flowRef, double headRef, double? npsha, int hz, int? pol)
        {
            return HydcalcZSelection.Search(flowRef, headRef, npsha, hz, pol).Select(s =>
                (s.Property.Field(0), s.Property.Field(1), s.Stages, s.Rpm,
                 s.Curve.MinDiameterMm.Value / s.Curve.MaxDiameterMm.Value,
                 s.Curve.MaxDiameterMm.Value * s.DiameterRatio, s.Curve.MaxDiameterMm.Value,
                 s.Curve.MinDiameterMm.Value, HydcalcZSelection.RatedEfficiency(s), s.Npshr,
                 PumpTableCache.Rows.First(r => r.PumpSize == s.Property.Field(1)).Clearance, s.Property.Field(9))).ToList();
        }
        public static double ReverseMaxFlow(
                                                QuadraticFitter maxHeadFit,
                                                double Qtrim,
                                                double Htrim,
                                                double Qmin,
                                                double Qmax,
                                                double tol = 1e-6,
                                                int maxIter = 100
                                            )
        {
            if (maxHeadFit == null) throw new ArgumentNullException(nameof(maxHeadFit));

            double a = Qmin;
            double b = Qmax;
            double fa = maxHeadFit.Evaluate(a) * Qtrim * Qtrim - Htrim * a * a;
            double fb = maxHeadFit.Evaluate(b) * Qtrim * Qtrim - Htrim * b * b;

            if (fa * fb > 0)
                throw new ArgumentException("No root in the specified Q range");

            double Qmid = 0;

            for (int i = 0; i < maxIter; i++)
            {
                Qmid = 0.5 * (a + b);
                double fmid = maxHeadFit.Evaluate(Qmid) * Qtrim * Qtrim - Htrim * Qmid * Qmid;

                if (Math.Abs(fmid) < tol)
                    return Qmid; // converged

                if (fmid * fa < 0)
                {
                    b = Qmid;
                    fb = fmid;
                }
                else
                {
                    a = Qmid;
                    fa = fmid;
                }
            }

            return Qmid; // best guess after maxIter
        }
        //efficiency calculation method
        //public static QuadraticFitterEq CreateTrimmedEfficiencyCurve_Volute(
        //                                                                        QuadraticFitterEq original,
        //                                                                        double d2,          // original impeller diameter
        //                                                                        double d2Trim,      // trimmed impeller diameter
        //                                                                        double epsilon = 0.20   // volute pump default
        //                                                                    )
        //{
        //    if (d2Trim >= d2)
        //        throw new ArgumentException("Trimmed diameter must be smaller than original.");

        //    if (epsilon < 0.15 || epsilon > 0.25)
        //        throw new ArgumentOutOfRangeException(
        //            nameof(epsilon),
        //            "For volute pumps, ε should be between 0.15 and 0.25.");

        //    double k = d2Trim / d2;
        //    double deltaEta = epsilon * (1.0 - k);

        //    return new QuadraticFitterEq
        //    {
        //        a = original.a / (k * k),
        //        b = original.b / k,
        //        c = original.c - deltaEta
        //    };
        //}

        public static double TrimmedEfficiencyAtFlow(
                                                    double Qt,        // target flow at trimmed impeller
                                                    double etaMax,    // evaluated efficiency of max impeller at equivalent flow
                                                    double Dfull,     // max impeller diameter
                                                    double Dtrim,     // trimmed impeller diameter
                                                    double Nref,      // reference RPM of etaMax
                                                    double Nt,        // actual operating RPM
                                                    double K = 0.75)  // trim loss factor
        {
            // Step 1: Map flow to equivalent full-impeller flow (if needed)
            // (here you must have etaMax at the Qref corresponding to Qt)
            // double Qref = Qt * (Dfull / Dtrim) * (Nref / Nt);
            // etaMax already evaluated at Qref

            // Step 2: Apply trim efficiency loss
            double ratio = Dtrim / Dfull;
            double etaTrim = etaMax * (1 - K * (1 - ratio));

            // Step 3: Clamp
            return Math.Max(0.0, etaTrim);
        }

        public static double TrimmedEfficiencyFromFitWithRPM(
                                                                double Qt,                // target flow at trimmed impeller
                                                                QuadraticFitter effFit,   // quadratic fit of max impeller at reference RPM
                                                                double Dfull,             // max impeller diameter
                                                                double Dtrim,             // trimmed impeller diameter
                                                                double Nref,              // reference RPM of the fit (e.g., 2950)
                                                                double Nt,                // actual operating RPM (e.g., 1450)
                                                                double K = 0.75)          // trim loss factor
        {
            if (effFit.a >= 0)
                throw new ArgumentException("Efficiency fit must open downward");

            // Step 1: Convert operating flow to equivalent flow at reference condition
            // Q_ref = Qt * (Dfull / Dtrim) * (Nref / Nt)
            double Qref = Qt * (Dfull / Dtrim) * (Nref / Nt);

            // Step 2: Evaluate efficiency from max impeller quadratic fit
            double eta0 = effFit.a * Qref * Qref
                        + effFit.b * Qref
                        + effFit.c;

            // Step 3: Apply trim efficiency loss (depends on diameter only)
            double ratio = Dtrim / Dfull;
            double etaTrim = eta0 * (1 - K * (1 - ratio));

            // Step 4: Clamp efficiency to [0, ηBEP0] (optional)
            return Math.Max(0.0, etaTrim);
        }


        public static QuadraticFitter BuildTrimmedImpellerCurve(
                                                                    QuadraticFitter maxHeadFit,
                                                                    double Qtrim,
                                                                    double Htrim,
                                                                    double QmaxReference, // a representative max impeller flow (any known point)
                                                                    int points = 20       // number of points to rebuild the curve
                                                                )
        {
            if (maxHeadFit == null)
                throw new ArgumentNullException(nameof(maxHeadFit));

            // Step 1: compute scaling ratio from the known trimmed point
            double r = Qtrim / QmaxReference;   // flow ratio
            double rH = Htrim / maxHeadFit.Evaluate(QmaxReference); // head ratio

            // Step 2: generate sample points along the max impeller curve
            double[] QmaxArray = new double[points];
            double[] HmaxArray = new double[points];
            double Qmin = 0.0;
            double Qstep = (QmaxReference - Qmin) / (points - 1);

            for (int i = 0; i < points; i++)
            {
                QmaxArray[i] = Qmin + i * Qstep;
                HmaxArray[i] = maxHeadFit.Evaluate(QmaxArray[i]);
            }

            // Step 3: scale points to trimmed impeller
            double[] QtrimArray = new double[points];
            double[] HtrimArray = new double[points];

            for (int i = 0; i < points; i++)
            {
                QtrimArray[i] = QmaxArray[i] * r;
                HtrimArray[i] = HmaxArray[i] * rH; // scaled to pass through known Htrim
            }

            // Step 4: fit new quadratic curve to trimmed points
            QuadraticFitter trimFit = new QuadraticFitter();
            trimFit.Fit(QtrimArray, HtrimArray);

            return trimFit;
        }


        //public static List<(string PumpSize, double Rpm, double trimDia)> foundPsize(
        //                                                                                double flowRef,
        //                                                                                double headRef,
        //                                                                                double npshr,
        //                                                                                int hz,
        //                                                                                int pol)
        //{
        //    var result = new List<(string PumpSize, double Rpm, double trimDia)>();

        //    // required speed from input


        //    foreach (var row in PumpTableCache.Rows)
        //    {
        //        // ---- Poles must match ----
        //        if (row.Poles != pol)
        //            continue;
        //        int requiredRpm = GetExpectedRpm(row.Rpm, hz);
        //        // ---- Per-row speed transfer ----
        //        double speedRatio = Convert.ToDouble(requiredRpm) / Convert.ToDouble(row.Rpm);

        //        // ---- Engineering sanity check ----
        //        if (speedRatio < 0.7 || speedRatio > 1.3)
        //            continue;

        //        // ---- Convert request to datasheet domain ----
        //        double flowData = flowRef / speedRatio;
        //        double headData = headRef / (speedRatio * speedRatio);
        //        double npshData = npshr / (speedRatio * speedRatio);

        //        // ---- Flow acceptance ----
        //        if (flowData > row.BepFlow)
        //            continue;

        //        if (flowData < row.MinFlow*1.2)
        //            continue;

        //        //if (flowData < (row.MinFlow + row.BepFlow) / 2 * 0.9)
        //        //    continue;

        //        // ---- Head screening ----
        //        if (row.Head < headData)
        //            continue;

        //        // ---- Curve cache ----
        //        if (!PumpCurveCache.Cache.TryGetValue(row.PumpSize, out var cached))
        //        {
        //            PumpData data = CentrifugalSearch.GetPumpCurve(row.PumpType, row.PumpSize);

        //            var headFit = new QuadraticFitter();
        //            headFit.Fit(data.Flow, data.Head);

        //            int i = 0;
        //            while (i < data.NPSHr.Length && data.NPSHr[i] == 0)
        //                i++;

        //            var npshFit = new QuadraticFitter();
        //            npshFit.Fit(
        //                data.Flow.Skip(i).ToArray(),
        //                data.NPSHr.Skip(i).ToArray());

        //            cached = new CachedPumpCurve
        //            {
        //                HeadFit = headFit,
        //                NpshFit = npshFit
        //            };

        //            PumpCurveCache.Cache[row.PumpSize] = cached;
        //        }

        //        // ---- Max impeller head ----
        //        double Hmax = cached.HeadFit.Evaluate(flowData) ;
        //        if (Hmax <= 0)
        //            continue;

        //        // ---- Diameter range ----
        //        ZDiaRange range = row.PumpSize == "50-450"
        //            ? new ZDiaRange(458, 380)
        //            : ZDiaTable.GetDiaRange(row.PumpSize);

        //        // ---- Required diameter ----
        //        double ratio = Math.Sqrt(headData / Hmax);
        //        double Dreq = range.MaxDia * ratio;

        //        if (Dreq < range.MinDia || Dreq > range.MaxDia)
        //            continue;

        //        // ---- Min trim check ----
        //        var trim = new MinImpellerCorrection(range.MinDia, range.MaxDia);
        //        if (trim.Head(Hmax) > headData)
        //            continue;

        //        // ---- NPSHr check ----
        //        double npshReq =
        //            cached.NpshFit.Evaluate(flowData) * Math.Pow(ratio, 1.5);

        //        if (npshReq > npshData)
        //            continue;


        //        // ---- Accept ----
        //        result.Add((row.PumpSize, row.Rpm, Dreq));
        //    }

        //    return result;
        //}





        //    public static List<string> foundPsize(
        //double flowRef,
        //double headRef,
        //double npshr,
        //int hz,
        //int pol)
        //    {
        //        var result = new List<string>();

        //        foreach (var row in PumpTableCache.Rows)
        //        {
        //            // ---- Speed & poles (UNCHANGED) ----
        //            if (row.Poles != pol || row.Hz != hz)
        //                continue;

        //            // ---- Flow acceptance (UNCHANGED) ----
        //            if (flowRef > row.BepFlow)
        //                continue;

        //            if (flowRef < row.MinFlow)
        //                continue;

        //            if (flowRef < (row.MinFlow + row.BepFlow) / 2 * 0.8)
        //                continue;

        //            // ---- Head table screening (UNCHANGED) ----
        //            if (row.Head < headRef)
        //                continue;

        //            // ---- Curve cache (UNCHANGED) ----
        //            if (!PumpCurveCache.Cache.TryGetValue(row.PumpSize, out var cached))
        //            {
        //                PumpData data = CentrifugalSearch.GetPumpCurve(row.PumpType, row.PumpSize);

        //                var headFit = new QuadraticFitter();
        //                headFit.Fit(data.Flow, data.Head);

        //                int i = 0;
        //                while (i < data.NPSHr.Length && data.NPSHr[i] == 0)
        //                    i++;

        //                var npshFit = new QuadraticFitter();
        //                npshFit.Fit(
        //                    data.Flow.Skip(i).ToArray(),
        //                    data.NPSHr.Skip(i).ToArray());

        //                cached = new CachedPumpCurve
        //                {
        //                    HeadFit = headFit,
        //                    NpshFit = npshFit
        //                };

        //                PumpCurveCache.Cache[row.PumpSize] = cached;
        //            }

        //            // ---- Max impeller head at flow ----
        //            double Hmax = cached.HeadFit.Evaluate(flowRef);
        //            if (Hmax <= 0)
        //                continue;

        //            // ---- Diameter range ----
        //            ZDiaRange range = row.PumpSize == "50-450"
        //                ? new ZDiaRange(458, 380)
        //                : ZDiaTable.GetDiaRange(row.PumpSize);

        //            // ---- Required impeller diameter (NEW, CORE) ----
        //            // H = Hmax * (D / Dmax)^2
        //            double ratio = Math.Sqrt(headRef / Hmax);
        //            double Dreq = range.MaxDia * ratio;

        //            // ---- Diameter feasibility ----
        //            if (Dreq < range.MinDia || Dreq > range.MaxDia)
        //                continue;

        //            // ---- Min diameter head check (UNCHANGED intent) ----
        //            var trim = new MinImpellerCorrection(range.MinDia, range.MaxDia);
        //            double Hmin = trim.Head(Hmax);
        //            if (Hmin > headRef)
        //                continue;

        //            // ---- NPSHr scaling with diameter (UPDATED, but equivalent intent) ----
        //            // NPSHr ∝ D^1.5
        //            double specificNpsh =
        //                cached.NpshFit.Evaluate(flowRef) * Math.Pow(ratio, 1.5);

        //            if (specificNpsh > npshr)
        //                continue;

        //            // ---- Accept ----
        //            result.Add(row.PumpSize);
        //        }

        //        return result;
        //    }

        //public static List<string> foundPsize(
        //                                        double flowRef,
        //                                        double headRef,
        //                                        double npshr,
        //                                        int hz,
        //                                        int pol)
        //{
        //    var result = new List<string>();

        //    foreach (var row in PumpTableCache.Rows)
        //    {
        //        if (row.Poles != pol || row.Hz != hz)
        //            continue;

        //        if (flowRef > row.BepFlow)
        //            continue;

        //        if (flowRef < row.MinFlow)
        //            continue;

        //        if (flowRef < (row.MinFlow + row.BepFlow) / 2 * 0.8)
        //            continue;

        //        if (row.Head < headRef)
        //            continue;

        //        if (!PumpCurveCache.Cache.TryGetValue(row.PumpSize, out var cached))
        //        {
        //            PumpData data = CentrifugalSearch.GetPumpCurve(row.PumpType, row.PumpSize);

        //            var headFit = new QuadraticFitter();
        //            headFit.Fit(data.Flow, data.Head);

        //            int i = 0;
        //            while (i < data.NPSHr.Length && data.NPSHr[i] == 0) i++;

        //            var npshFit = new QuadraticFitter();
        //            npshFit.Fit(
        //                data.Flow.Skip(i).ToArray(),
        //                data.NPSHr.Skip(i).ToArray());

        //            cached = new CachedPumpCurve
        //            {
        //                HeadFit = headFit,
        //                NpshFit = npshFit
        //            };

        //            PumpCurveCache.Cache[row.PumpSize] = cached;
        //        }

        //        double Hmax = cached.HeadFit.Evaluate(flowRef);

        //        ZDiaRange range = row.PumpSize == "50-450"
        //            ? new ZDiaRange(458, 380)
        //            : ZDiaTable.GetDiaRange(row.PumpSize);

        //        var trim = new MinImpellerCorrection(range.MinDia, range.MaxDia);
        //        double Hmin = trim.Head(Hmax);

        //        if (Hmin > headRef)
        //            continue;

        //        double specificNpsh = cached.NpshFit.Evaluate(flowRef);
        //        if (specificNpsh > npshr)
        //            continue;

        //        result.Add(row.PumpSize);
        //    }

        //    return result;
        //}

        //public static List<string> foundPsize(
        //                                        double flowRef,
        //                                        double headRef,
        //                                        double npshr,
        //                                        int hz,
        //                                        int pol)
        //{
        //    const int COL_PUMP_TYPE = 4;
        //    const int COL_PUMP_SIZE = 6;
        //    const int COL_FLOW_BEP = 34;
        //    const int COL_HEAD = 28;
        //    const int COL_NPSH = 29;
        //    const int COL_MAXZ = 26;
        //    const int COL_MINFLOW = 41;
        //    const int COL_POL_AJ = 36;
        //    const int COL_HZ_AK = 37;

        //    List<string> result = new List<string>();

        //    OfficeOpenXml.ExcelPackage.LicenseContext =
        //        OfficeOpenXml.LicenseContext.NonCommercial;

        //    using (var ms = new MemoryStream(Properties.Resources.HydraulicTable))
        //    using (var package = new ExcelPackage(ms))
        //    {
        //        var ws = package.Workbook.Worksheets["Z"];
        //        if (ws == null) return result;

        //        int lastRow = ws.Dimension.End.Row;

        //        for (int r = 2; r <= lastRow; r++)
        //        {
        //            // ---- Quick reject: pol / hz ----
        //            if (!int.TryParse(ws.Cells[r, COL_POL_AJ].Text, out int polAJ) || polAJ != pol)
        //                continue;

        //            if (!int.TryParse(ws.Cells[r, COL_HZ_AK].Text, out int hzAK) || hzAK != hz)
        //                continue;

        //            // ---- Parse once ----
        //            if (!double.TryParse(ws.Cells[r, COL_FLOW_BEP].Text, out double bepFlow))
        //                continue;
        //            if (!double.TryParse(ws.Cells[r, COL_MINFLOW].Text, out double minFlow))
        //                continue;

        //            // ---- Flow acceptance zone (UNCHANGED) ----
        //            if (flowRef > bepFlow)
        //                continue;
        //            if (flowRef < minFlow)
        //                continue;
        //            if (flowRef < (minFlow + bepFlow) / 2 * 0.8)
        //                continue;

        //            if (!double.TryParse(ws.Cells[r, COL_HEAD].Text, out double head))
        //                continue;
        //            if (head < headRef)
        //                continue;

        //            if (!double.TryParse(ws.Cells[r, COL_NPSH].Text, out double tableNpsh))
        //                continue;

        //            string pumpType = ws.Cells[r, COL_PUMP_TYPE].Text;
        //            string pumpSize = ws.Cells[r, COL_PUMP_SIZE].Text;

        //            // ---- Curve cache ----
        //            if (!PumpCurveCache.Cache.TryGetValue(pumpSize, out var cached))
        //            {
        //                PumpData pumpData = CentrifugalSearch.GetPumpCurve(pumpType, pumpSize);

        //                var headFit = new QuadraticFitter();
        //                headFit.Fit(pumpData.Flow, pumpData.Head);

        //                int idx = 0;
        //                while (idx < pumpData.NPSHr.Length && pumpData.NPSHr[idx] == 0)
        //                    idx++;

        //                double[] flowTrim = pumpData.Flow.Skip(idx).ToArray();
        //                double[] npshTrim = pumpData.NPSHr.Skip(idx).ToArray();

        //                var npshFit = new QuadraticFitter();
        //                npshFit.Fit(flowTrim, npshTrim);

        //                cached = new CachedPumpCurve
        //                {
        //                    HeadF
    }

}
