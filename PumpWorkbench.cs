using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using PumpSelectionTools;
using ZTools.Offline;

public sealed record PumpRequest(double Flow, double Head, double Density = 1000, int Hz = 50,
    int? Rpm = null, int? Poles = null, double? Npsha = null, double? Temperature = null,
    string? Family = null, string? ApiType = null, double? ViscosityCp = null, string? XSubtype = null)
{
    internal double KinematicViscosity()
    {
        if (ViscosityCp is not double value) return 1;
        if (!double.IsFinite(Density) || Density <= 0)
            throw new ArgumentException("Density must be greater than zero to convert viscosity.");
        double cst = value * (1000 / Density);
        if (!double.IsFinite(cst) || cst <= 0)
            throw new ArgumentException("Viscosity must be a positive number in cP.");
        if (cst > 3000)
            throw new ArgumentException("Viscosity exceeds 3,000 cSt after conversion using density. Request manufacturer data.");
        return cst;
    }
}
public sealed record CurveRequest(PumpRequest Duty, int Index, int Width = 960, double PreferredMin = .7, double PreferredMax = 1.2);
public sealed record CurvePdfRequest(PumpRequest Duty, int Index, PumpProjectDetails? Details);
public sealed record PumpTrimSample(double FlowM3h, double? HeadM, double? ShaftPowerKw, double? EfficiencyPercent,
    double? NpshrM, string? NpshrSource);
public sealed record PumpTrimPoint(double Diameter, double? Head, double? Efficiency, double? Npshr, string? NpshrSource,
    bool NpshrInterpolated, PumpTrimSample[] Samples);
public sealed record PumpTrimPreview(string Series, string Size, string Curve, int Rpm, int SourceRpm,
    string SourceFile, int SourcePage, double Flow, double RequestedHead, PumpTrimPoint[] Points, string[] Plots);
public sealed record PumpHit(int Index, string Family, string Size, string ApiType, string Suction,
    string FlowType, string Group, string Curve, int Stages, int Rpm, double MinDiameter, double Diameter,
    double MaxDiameter, double Head, double Efficiency, double Npshr, double Power, double? MotorKw,
    string Orientation, string OriginalCurve, string Series, string? XSubtype = null,
    int? SourceRpm = null, bool InterpolatedTrim = false, string? NpshrSource = null)
{
    public string ModelSeries => XSubtype ?? Series;
}

public sealed class PumpWorkbench
{
    private static readonly string[] XSubtypes = ["XA", "XE", "XF", "XZX", "XGP"];

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
        var catalog = HydcalcCatalog.Bundled;
        var sourceFamilies = catalog.Properties.Select(p => p.Field(0)).Append("Z").Distinct()
            .Where(family => SeriesEnabled(family, catalog.GetSeries(family))).ToArray();
        var families = sourceFamilies.Select(catalog.GetSeries).Distinct().Order().ToArray();
        var apiTypes = sourceFamilies.Select(PumpFamilyPolicy.GetApiTypeLabel)
            .SelectMany(type => type.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(type => type.Length > 0).Distinct().Order().ToList();
        apiTypes.RemoveAll(type => type is "ISO" or "ANSY" or "\u6d41\u7a0b\u6cf5");
        if (sourceFamilies.Contains("CZ")) apiTypes.Add(PumpFamilyPolicy.GetApiTypeLabel("CZ"));
        return new { families, apiTypes, xSubtypes = XSubtypes };
    }

    public object Catalog(string? family, string? query, int page)
    {
        bool Contains(string value) => string.IsNullOrWhiteSpace(query) ||
            value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
        static string DisplaySeries(HydcalcCurve curve) => curve.AvailableForSelection ? curve.Series : curve.Family;
        var all = HydcalcCatalog.Bundled.Curves;
        var curves = all.Where(curve =>
            (MatchesSeries(family, curve.Family, DisplaySeries(curve)) ||
                (!curve.AvailableForSelection && string.Equals(family?.Trim(), HydcalcCatalog.UnavailableSeries, StringComparison.OrdinalIgnoreCase))) &&
            (Contains(curve.CurveNumber) || Contains(curve.OriginalCurveNumber) || Contains(curve.Name) || Contains(curve.Size)));
        return new
        {
            total = curves.Count(), page, pageSize = 30,
            families = all.Select(DisplaySeries).Distinct().Order(),
            records = curves.Skip((page - 1) * 30).Take(30).Select(curve => new
            {
                curve.Id, curve.Family, series = DisplaySeries(curve), curve.AvailableForSelection,
                curve.Size, curve.Name, curve.CurveNumber, curve.OriginalCurveNumber, curve.Rpm, curve.MinDiameterMm,
                curve.MaxDiameterMm, points = curve.Points.Count, curve.Issues
            })
        };
    }

    public PumpHit[] Search(PumpRequest duty)
    {
        var matches = Find(duty, out var subtype);
        return matches.Select((item, index) => Summarize(item, duty.Density, duty.Hz, index, subtype)).ToArray();
    }

    public PumpTrimPreview[] TrimPreview(PumpRequest duty) => BuildTrimPreviews(duty, includePlots: true);

    private static PumpTrimPreview[] BuildTrimPreviews(PumpRequest duty, bool includePlots)
    {
        if (string.IsNullOrWhiteSpace(duty.Family) || !double.IsFinite(duty.Flow) || duty.Flow <= 0 ||
            !double.IsFinite(duty.Head) || duty.Head <= 0 || duty.KinematicViscosity() > 1)
            return [];

        var previews = new List<PumpTrimPreview>();
        foreach (var curve in HydcalcCatalog.Bundled.Curves)
        {
            if (!curve.AvailableForSelection || !MatchesSeries(duty.Family, curve.Family, curve.Series) ||
                !PumpFamilyPolicy.MatchesApiType(curve.Family, duty.ApiType) ||
                !curve.Metadata.TryGetValue("ReferenceDiametersMm", out var diameters) ||
                curve.MaxDiameterMm is not > 0 || curve.Rpm is not > 0 || curve.Issues.Count != 0)
                continue;

            var reference = PdfTrimCurves.For(curve.CurveNumber);
            if (reference == null) continue;
            var rpm = duty.Rpm ?? (int)Math.Round(curve.Rpm.Value);
            if (rpm != reference.Rpm && Math.Abs(rpm - curve.Rpm.Value) > 1e-6) continue;
            if (!HydcalcSizing.Speeds.Keys.Any(motor =>
            {
                double synchronous = 120.0 * duty.Hz / motor.Item2;
                return motor.Item1 == duty.Hz && (duty.Poles == null || duty.Poles == motor.Item2) &&
                    rpm >= .90 * synchronous && rpm <= 1.02 * synchronous;
            })) continue;

            var listed = ParseDiameters(diameters);
            var points = reference.Impellers.Where(impeller => listed.Contains(impeller.DiameterMm))
                .Select(impeller => ReadTrimPoint(curve, reference, impeller, rpm, duty.Flow)).ToArray();
            if (points.Length != 0)
                previews.Add(new(curve.Series, curve.Size, curve.CurveNumber, rpm, reference.Rpm, reference.SourceFile,
                    reference.Page, duty.Flow, duty.Head, points, includePlots ? TrimPlots(points, duty.Flow) : []));
        }
        return previews.ToArray();
    }

    private static double[] ParseDiameters(string? value) => (value ?? "").Split(';')
        .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var diameter) ? diameter : 0)
        .Where(diameter => double.IsFinite(diameter) && diameter > 0).Distinct().ToArray();

    private static PumpTrimPoint ReadTrimPoint(HydcalcCurve original, PdfTrimCurves reference,
        PdfImpeller impeller, int rpm, double ratedFlow)
    {
        var samples = reference.Samples(original, rpm, impeller.DiameterMm, ratedFlow)
            .Select(p => new PumpTrimSample(p.Flow, p.Head, p.WaterPower, p.Efficiency, p.Npshr, p.NpshrSource)).ToArray();
        var duty = samples.First(point => point.FlowM3h == ratedFlow);
        return new(impeller.DiameterMm, duty.HeadM, duty.EfficiencyPercent, duty.NpshrM, duty.NpshrSource,
            impeller.Npshr.Length == 0, samples);
    }

    private static string[] TrimPlots(PumpTrimPoint[] points, double dutyFlow)
    {
        var plotSpecs = new (string Title, string Axis, Func<PumpTrimSample, double?> Value)[]
        {
            ("Head by impeller diameter", "Head (m)", row => row.HeadM),
            ("Efficiency (derived from chart H and power)", "Efficiency (%)", row => row.EfficiencyPercent),
            ("NPSHr (dashed = diameter interpolation)", "NPSHr (m)", row => row.NpshrM)
        };
        OxyColor[] colors = [OxyColors.Black, OxyColor.FromRgb(0, 102, 173), OxyColor.FromRgb(186, 59, 29),
            OxyColor.FromRgb(0, 123, 98), OxyColor.FromRgb(136, 96, 0)];
        double maxFlow = points.Max(point => point.Samples.Max(sample => sample.FlowM3h));
        double flowLimit = Math.Ceiling(maxFlow / 10) * 10;
        var rendered = new string[plotSpecs.Length];
        for (int index = 0; index < plotSpecs.Length; index++)
        {
            var spec = plotSpecs[index];
            var model = new PlotModel { Title = spec.Title, TitleFontSize = 14, PlotMargins = new OxyThickness(69, double.NaN, 19, 36) };
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Flow (m3/h)", Minimum = 0, Maximum = flowLimit,
                MajorGridlineStyle = LineStyle.Dot });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = spec.Axis, Minimum = 0,
                AxisTitleDistance = 12, MaximumPadding = .06, MajorGridlineStyle = LineStyle.Dot });
            if (index == 0)
                model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopCenter, LegendPlacement = LegendPlacement.Outside,
                    LegendOrientation = LegendOrientation.Horizontal, LegendFontSize = 11, LegendItemSpacing = 8 });
            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
                var line = new LineSeries { Title = "D" + point.Diameter.ToString("0.##", CultureInfo.InvariantCulture),
                    Color = colors[i % colors.Length], LineStyle = index == 2 && point.NpshrInterpolated ? LineStyle.Dash : LineStyle.Solid,
                    StrokeThickness = 1.8 };
                line.Points.AddRange(point.Samples.Select(sample => spec.Value(sample) is double value
                    ? new DataPoint(sample.FlowM3h, value) : DataPoint.Undefined));
                model.Series.Add(line);
            }
            PlotCurveFit.Apply(model, index == 2 ? dutyFlow : null);
            rendered[index] = ExportPlot(model, 620, index == 0 ? 275 : 205);
        }
        return rendered;
    }

    internal static WorkbenchDownload ExportTrimData(PumpRequest duty)
    {
        var previews = BuildTrimPreviews(duty, includePlots: false);
        if (previews.Length == 0) throw new ArgumentException("当前工况没有可导出的叶轮估算数据。");
        var data = new
        {
            purpose = "Preliminary estimates only. Do not use for final selection or NPSH verification.",
            model = "Head and shaft power digitized from the PDF. Water efficiency from hydraulic power / shaft power. " +
                "NPSHr for intermediate diameters interpolated at equal source flow. Earlier supplied D330 data fills gaps in its PDF trace. " +
                "Speed correction uses Q times n/n0, head and NPSHr times (n/n0)^2, and power times (n/n0)^3.",
            curves = previews.Select(curve => new
            {
                curve.Series, curve.Size, curve.Curve, curve.Rpm, curve.SourceRpm, curve.SourceFile, curve.SourcePage,
                impellers = curve.Points.Select(point => new { diameterMm = point.Diameter, point.NpshrInterpolated, point.Samples })
            })
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return new("Impeller_Trim_Curves.json", "application/json", new MemoryStream(bytes, writable: false));
    }

    public PumpHit GetSelection(PumpRequest duty, int index)
    {
        var matches = Find(duty, out var subtype);
        if (index < 0 || index >= matches.Count) throw new ArgumentException("Select a valid pump result.");
        return Summarize(matches[index], duty.Density, duty.Hz, index, subtype);
    }

    public object Curves(CurveRequest request)
    {
        var (plots, summary, report) = BuildReport(request);
        var width = Math.Clamp(request.Width, 360, 1200);
        return new
        {
            pump = summary,
            report,
            plots = plots.Select((plot, index) => ExportPlot(plot, width, index == 0 ? 380 : 190)).ToArray(),
            printPlots = plots.Select(ExportPrintPlot).ToArray()
        };
    }

    internal static WorkbenchDownload ExportPdfBatch(CurvePdfRequest[] requests, CancellationToken cancellation = default)
    {
        if (requests is not { Length: >= 1 and <= 30 } || requests.Any(request => request?.Duty == null))
            throw new ArgumentException("批量导出须包含 1 至 30 项有效选型。");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                cancellation.ThrowIfCancellationRequested();
                using var document = CreateCurvePdf(request);
                string stem = "Curve/" + Path.GetFileNameWithoutExtension(document.Name);
                string path = stem + ".pdf";
                for (int suffix = 2; !used.Add(path); suffix++) path = stem + $" ({suffix}).pdf";
                using var entry = zip.CreateEntry(path).Open();
                document.Content.CopyTo(entry);
            }
        }
        return new("Curves_V0.zip", "application/zip", new MemoryStream(output.ToArray(), writable: false));
    }

    internal static WorkbenchDownload CreateCurvePdf(CurvePdfRequest request)
    {
        var (plots, pump, report) = BuildReport(new(request.Duty, request.Index));
        string name = ReportFileName.Curve(request.Details?.Tag, request.Details?.Service, pump.ModelSeries + pump.Size, pump.Rpm);
        return new(name + ".pdf", "application/pdf",
            new MemoryStream(PumpCurvePdf.Create(plots, pump, request.Duty, report, request.Details), writable: false));
    }

    private static (PlotModel[] Plots, PumpHit Pump, PumpReportMetrics Report) BuildReport(CurveRequest request)
    {
        var results = Find(request.Duty, out var subtype);
        if (request.Index < 0 || request.Index >= results.Count) throw new ArgumentException("Select a valid pump result.");
        if (!double.IsFinite(request.PreferredMin) || !double.IsFinite(request.PreferredMax) ||
            request.PreferredMin < 0 || request.PreferredMax <= request.PreferredMin)
            throw new ArgumentException("Preferred flow limits are invalid.");
        var pump = results[request.Index];
        var density = request.Duty.Density;
        var summary = Summarize(pump, density, request.Duty.Hz, request.Index, subtype);
        var plots = HydcalcSelectionPlotter.Create(pump, density, Efficiency(pump),
            preferredMin: request.PreferredMin, preferredMax: request.PreferredMax, series: summary.ModelSeries);
        var flowPlots = pump.ViscosityCst > 1 ? plots : new[] { plots[0] };
        var maximum = flowPlots.SelectMany(plot => plot.Series.OfType<LineSeries>()).SelectMany(series => series.Points)
            .Where(point => double.IsFinite(point.X)).Max(point => point.X);
        var stepEstimate = maximum / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(stepEstimate)));
        var step = new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }.First(value => value * magnitude >= stepEstimate) * magnitude;
        var limit = Math.Ceiling(maximum / step) * step;
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

        var report = PumpReportMetrics.Read(plots, summary, request.Duty, pump, MotorSizingMargin(summary.Power));
        for (int index = 0; index < plots.Length; index++)
            PlotCurveFit.Apply(plots[index], index == 1 && pump.PdfReference != null ? pump.Flow : null);
        return (plots, summary, report);
    }

    private static string ExportPlot(PlotModel plot, int width, int height) =>
        new SvgExporter { Width = width, Height = height, IsDocument = false }.ExportToString(plot);

    private static string ExportPrintPlot(PlotModel plot, int index)
    {
        if (index != 0)
        {
            foreach (var axis in plot.Axes.Where(axis => axis.Position is AxisPosition.Left or AxisPosition.Right))
            {
                axis.FontSize = 10;
                axis.TitleFontSize = 10;
                axis.AxisTitleDistance += 6;
            }
        }
        return ExportPlot(plot, 735, index == 0 ? 365 : 130);
    }

    private static double Efficiency(HydcalcSelection item) =>
        item.Property.Field(0) == "Z" ? HydcalcZSelection.RatedEfficiency(item) : item.Efficiency;

    private static PumpHit Summarize(HydcalcSelection item, double density, int hz, int index, string? subtype)
    {
        var family = item.Property.Field(0);
        var efficiency = Efficiency(item);
        var power = density * 9.81 * item.Flow / 3600 * item.Head / (efficiency / 100) / 1000;
        var motor = MotorRating(power, item.Rpm, hz);
        var minimum = item.PdfReference?.MinDiameter ?? item.Curve.MinDiameterMm!.Value;
        var maximum = item.PdfReference?.MaxDiameter ?? item.Curve.MaxDiameterMm!.Value;
        if (item.SuctionCurve != null)
            minimum = Math.Max(minimum, item.SuctionCurve.MinDiameterMm!.Value / item.SuctionCurve.MaxDiameterMm!.Value * maximum);
        return new PumpHit(index, family, item.Property.Field(1), PumpFamilyPolicy.GetApiTypeLabel(family),
            PumpFamilyPolicy.GetSuctionLabel(family), item.FlowType, item.HydraulicGroup,
            item.HydraulicLabel, item.Stages, item.Rpm, minimum, item.DiameterRatio * maximum,
            maximum, item.Head, efficiency, item.Npshr, power, motor,
            item.Property.Field(9), item.OriginalHydraulicLabel, HydcalcCatalog.Bundled.GetSeries(family), subtype,
            item.PdfReference?.Rpm, item.PdfReference?.IsPublishedDiameter(item.DiameterRatio * maximum) == false,
            item.NpshrSource);
    }

    private static double? MotorRating(double power, int rpm, int hz)
    {
        if (hz != 50) return null;
        var synchronous = MotorRatings50Hz.Keys.MinBy(speed => Math.Abs((double)rpm / speed - 1));
        if (rpm < synchronous * .90 || rpm > synchronous * 1.02) return null;
        var required = power * (1 + MotorSizingMargin(power) / 100);
        return MotorRatings50Hz[synchronous].Where(rating => rating >= required).Select(rating => (double?)rating).FirstOrDefault();
    }

    private static double MotorSizingMargin(double power) => power <= 22 ? 25 : power >= 55 ? 10 : 15;

    private static bool SeriesEnabled(string family, string series) => PumpFamilyPolicy.IsEnabled(family) &&
        !string.IsNullOrWhiteSpace(series) && !string.Equals(series, HydcalcCatalog.UnavailableSeries, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSeries(string? filter, string family, string series) =>
        string.IsNullOrWhiteSpace(filter) || string.Equals(series, filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(family, filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? ResolveXSubtype(PumpRequest duty)
    {
        if (string.IsNullOrWhiteSpace(duty.XSubtype)) return null;
        if (string.IsNullOrWhiteSpace(duty.Family) || !MatchesSeries(duty.Family, "Z", "X"))
            throw new ArgumentException("X 子型号仅适用于 X 系列。");
        return XSubtypes.FirstOrDefault(type => string.Equals(type, duty.XSubtype.Trim(), StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("X 子型号必须为 XA、XE、XF、XZX 或 XGP。");
    }

    private static List<HydcalcSelection> Find(PumpRequest duty, out string? subtype)
    {
        subtype = ResolveXSubtype(duty);
        if (duty.Poles is int poles && !HydcalcSizing.Speeds.ContainsKey(Tuple.Create(duty.Hz, poles)))
            throw new ArgumentException("Choose a supported pole count.");
        double viscosity = duty.KinematicViscosity();
        var input = new HydcalcDuty
        {
            Flow = duty.Flow, Head = duty.Head, Density = duty.Density, Hz = duty.Hz, FixedRpm = duty.Rpm,
            Poles = duty.Poles, Npsha = duty.Npsha, TemperatureC = duty.Temperature, ViscosityCst = viscosity
        };
        var catalog = HydcalcCatalog.Bundled;
        var imported = HydcalcSizing.Search(catalog, input);
        var zPumps = HydcalcZSelection.Search(duty.Flow, duty.Head, duty.Npsha, duty.Hz, duty.Poles, duty.Rpm, viscosity);
        return zPumps.Concat(imported).Where(item =>
        {
            var family = item.Property.Field(0);
            var series = catalog.GetSeries(family);
            return SeriesEnabled(family, series) && item.Curve.AvailableForSelection &&
                item.SuctionCurve?.AvailableForSelection != false && PumpFamilyPolicy.MatchesApiType(family, duty.ApiType) &&
                MatchesSeries(duty.Family, family, series);
        }).OrderByDescending(Efficiency).ThenBy(item => item.Property.Field(0)).ThenBy(item => item.Property.Field(1)).ToList();
    }
}
