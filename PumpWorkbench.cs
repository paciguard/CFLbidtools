using System.Globalization;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using PumpSelectionTools;

public sealed record PumpRequest(double Flow, double Head, double Density = 1000, int Hz = 50,
    int? Rpm = null, int? Poles = null, double? Npsha = null, double? Temperature = null,
    string? Family = null, string? ApiType = null);
public sealed record CurveRequest(PumpRequest Duty, int Index, int Width = 960, double PreferredMin = .7, double PreferredMax = 1.2);
public sealed record PumpHit(int Index, string Family, string Size, string ApiType, string Suction,
    string FlowType, string Group, string Curve, int Stages, int Rpm, double MinDiameter, double Diameter,
    double MaxDiameter, double Head, double Efficiency, double Npshr, double Power, double? MotorKw,
    string Orientation);

public sealed class PumpWorkbench
{
    private static readonly Dictionary<int, double[]> MotorRatings50Hz = new()
    {
        [3000] = [.75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250, 280, 315, 355, 375],
        [1500] = [.55, .75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250, 280, 315, 355, 375],
        [1000] = [.37, .55, .75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250, 280, 315],
        [750] = [.75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250],
        [600] = [15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200],
        [500] = [18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185],
        [429] = [18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160],
        [375] = [22, 30, 37, 45, 55, 75, 90, 110, 132]
    };

    public PumpWorkbench()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            PumpTableCache.Load();
            PumpCurveDataCache.Load();
            foreach (var size in PumpTableCache.Rows.Where(row => row.PumpType == "Z").Select(row => row.PumpSize).Distinct())
                HydcalcZSelection.MatchCurve(size);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    public object Options()
    {
        var families = HydcalcCatalog.Bundled.Properties.Select(p => p.Field(0)).Append("Z")
            .Where(PumpFamilyPolicy.IsEnabled).Distinct().Order().ToArray();
        var apiTypes = families.Select(PumpFamilyPolicy.GetApiTypeLabel)
            .SelectMany(type => type.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(type => type.Length > 0).Distinct().Order().ToList();
        apiTypes.RemoveAll(type => type is "ISO" or "ANSY" or "\u6d41\u7a0b\u6cf5");
        if (families.Contains("CZ")) apiTypes.Add(PumpFamilyPolicy.GetApiTypeLabel("CZ"));
        return new { families, apiTypes };
    }

    public object Catalog(string? family, string? query, int page)
    {
        bool Contains(string value) => string.IsNullOrWhiteSpace(query) ||
            value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
        var curves = HydcalcCatalog.Bundled.Curves.Where(curve =>
            (string.IsNullOrWhiteSpace(family) || curve.Family.Equals(family, StringComparison.OrdinalIgnoreCase)) &&
            (Contains(curve.Name) || Contains(curve.Size)));
        return new
        {
            total = curves.Count(), page, pageSize = 30,
            families = HydcalcCatalog.Bundled.Curves.Select(curve => curve.Family).Distinct().Order(),
            records = curves.Skip((page - 1) * 30).Take(30).Select(curve => new
            {
                curve.Id, curve.Family, curve.Size, curve.Name, curve.Rpm, curve.MinDiameterMm,
                curve.MaxDiameterMm, points = curve.Points.Count, curve.Issues
            })
        };
    }

    public PumpHit[] Search(PumpRequest duty) =>
        Find(duty).Select((item, index) => Summarize(item, duty.Density, duty.Hz, index)).ToArray();

    public PumpHit GetSelection(PumpRequest duty, int index)
    {
        var matches = Find(duty);
        if (index < 0 || index >= matches.Count) throw new ArgumentException("Select a valid pump result.");
        return Summarize(matches[index], duty.Density, duty.Hz, index);
    }

    public object Curves(CurveRequest request)
    {
        var results = Find(request.Duty);
        if (request.Index < 0 || request.Index >= results.Count) throw new ArgumentException("Select a valid pump result.");
        if (!double.IsFinite(request.PreferredMin) || !double.IsFinite(request.PreferredMax) ||
            request.PreferredMin < 0 || request.PreferredMax <= request.PreferredMin)
            throw new ArgumentException("Preferred flow limits are invalid.");
        var pump = results[request.Index];
        var density = request.Duty.Density;
        var plots = HydcalcSelectionPlotter.Create(pump, density, Efficiency(pump),
            preferredMin: request.PreferredMin, preferredMax: request.PreferredMax);
        var maximum = plots[0].Series.OfType<LineSeries>().SelectMany(series => series.Points)
            .Where(point => double.IsFinite(point.X)).Max(point => point.X);
        var stepEstimate = maximum / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(stepEstimate)));
        var step = new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }.First(value => value * magnitude >= stepEstimate) * magnitude;
        var limit = Math.Ceiling(maximum / step) * step;
        var width = Math.Clamp(request.Width, 360, 1200);
        foreach (var plot in plots)
        {
            var flow = plot.Axes.Single(axis => axis.Position == AxisPosition.Bottom);
            flow.Minimum = 0;
            flow.Maximum = limit;
            flow.MajorStep = step;
            flow.MinorStep = step / 5;
            plot.PlotMargins = new OxyThickness(58, double.NaN, 65, 45);
            plot.Background = OxyColors.Transparent;
        }

        return new
        {
            pump = Summarize(pump, density, request.Duty.Hz, request.Index),
            plots = plots.Select((plot, index) => new SvgExporter
            {
                Width = width,
                Height = index == 0 ? 380 : 190,
                IsDocument = false
            }.ExportToString(plot)).ToArray()
        };
    }

    private static double Efficiency(HydcalcSelection item) =>
        item.Property.Field(0) == "Z" ? HydcalcZSelection.RatedEfficiency(item) : item.Efficiency;

    private static PumpHit Summarize(HydcalcSelection item, double density, int hz, int index)
    {
        var family = item.Property.Field(0);
        var efficiency = Efficiency(item);
        var power = density * 9.81 * item.Flow / 3600 * item.Head / (efficiency / 100) / 1000;
        var motor = MotorRating(power, item.Rpm, hz);
        var minimum = item.Curve.MinDiameterMm!.Value;
        var maximum = item.Curve.MaxDiameterMm!.Value;
        if (item.SuctionCurve != null)
            minimum = Math.Max(minimum, item.SuctionCurve.MinDiameterMm!.Value / item.SuctionCurve.MaxDiameterMm!.Value * maximum);
        return new PumpHit(index, family, item.Property.Field(1), PumpFamilyPolicy.GetApiTypeLabel(family),
            PumpFamilyPolicy.GetSuctionLabel(family), item.FlowType, item.HydraulicGroup,
            item.HydraulicLabel, item.Stages, item.Rpm, minimum, item.DiameterRatio * maximum,
            maximum, item.Head, efficiency, item.Npshr, power, motor,
            item.Property.Field(9));
    }

    private static double? MotorRating(double power, int rpm, int hz)
    {
        if (hz != 50) return null;
        var synchronous = MotorRatings50Hz.Keys.MinBy(speed => Math.Abs((double)rpm / speed - 1));
        if (rpm < synchronous * .90 || rpm > synchronous * 1.02) return null;
        var required = power * (power <= 22 ? 1.25 : power >= 55 ? 1.10 : 1.15);
        return MotorRatings50Hz[synchronous].Where(rating => rating >= required).Select(rating => (double?)rating).FirstOrDefault();
    }

    private static List<HydcalcSelection> Find(PumpRequest duty)
    {
        if (duty.Poles is int poles && !HydcalcSizing.Speeds.ContainsKey(Tuple.Create(duty.Hz, poles)))
            throw new ArgumentException("Choose a supported pole count.");
        var input = new HydcalcDuty
        {
            Flow = duty.Flow, Head = duty.Head, Density = duty.Density, Hz = duty.Hz, FixedRpm = duty.Rpm,
            Poles = duty.Poles, Npsha = duty.Npsha, TemperatureC = duty.Temperature
        };
        var imported = HydcalcSizing.Search(HydcalcCatalog.Bundled, input);
        var zPumps = HydcalcZSelection.Search(duty.Flow, duty.Head, duty.Npsha, duty.Hz, duty.Poles, duty.Rpm);
        return zPumps.Concat(imported).Where(item =>
        {
            var family = item.Property.Field(0);
            return PumpFamilyPolicy.IsEnabled(family) && PumpFamilyPolicy.MatchesApiType(family, duty.ApiType) &&
                (string.IsNullOrWhiteSpace(duty.Family) || family.Equals(duty.Family, StringComparison.OrdinalIgnoreCase));
        }).OrderByDescending(Efficiency).ThenBy(item => item.Property.Field(0)).ThenBy(item => item.Property.Field(1)).ToList();
    }
}
